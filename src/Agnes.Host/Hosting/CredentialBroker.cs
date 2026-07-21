using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>What a sandboxed session is allowed to authenticate to, and how strictly.</summary>
/// <param name="SessionId">The session the grant belongs to (for audit routing), or null.</param>
/// <param name="HostPattern">The allowed host — an exact host ("github.com") or "*".</param>
/// <param name="RepoPattern">The allowed repo "owner/repo", or null/"*" for any repo on the host.</param>
/// <param name="Mode">"Ask" (gate each use via the permission card) or "Trust" (auto-allow + audit).</param>
public sealed record CredentialGrant(string? SessionId, string HostPattern, string? RepoPattern, string Mode)
{
    /// <summary>Whether this grant covers the requested host + repo.</summary>
    public bool Covers(CredentialRequest request)
    {
        var hostOk = HostPattern == "*" || string.Equals(HostPattern, request.Host, StringComparison.OrdinalIgnoreCase);
        if (!hostOk)
        {
            return false;
        }

        if (string.IsNullOrEmpty(RepoPattern) || RepoPattern == "*")
        {
            return true;
        }

        // Scoped to a specific repo: git must have told us which repo (useHttpPath), and it must match.
        return request.Repo is not null && string.Equals(RepoPattern, request.Repo, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Per-session credential grants: a short-lived token (baked into the sandbox's env) maps to what that
/// session may authenticate to. In-memory only, mirroring the ephemeral sandboxes (and the MCP forward).
/// </summary>
public sealed class CredentialBrokerRegistry
{
    private readonly ConcurrentDictionary<string, CredentialGrant> _byToken = new();

    /// <summary>Grants a session a scope; returns the token the guest git helper presents.</summary>
    public string Register(CredentialGrant grant)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _byToken[token] = grant;
        return token;
    }

    public CredentialGrant? Resolve(string? token)
        => token is not null && _byToken.TryGetValue(token, out var grant) ? grant : null;

    public string? SessionFor(string? token)
        => token is not null && _byToken.TryGetValue(token, out var grant) ? grant.SessionId : null;

    public void Unregister(string? token)
    {
        if (token is not null)
        {
            _byToken.TryRemove(token, out _);
        }
    }
}

/// <summary>
/// The registered credential sources (a stored PAT, a linked GitHub App). Mutable so a source can be
/// added at runtime — e.g. right after the user completes the Connect-GitHub flow.
/// </summary>
public sealed class CredentialSourceRegistry
{
    private readonly List<ICredentialSource> _sources = new();
    private readonly object _lock = new();

    /// <summary>Adds a source, replacing any existing source of the same runtime type.</summary>
    public void Set(ICredentialSource source)
    {
        lock (_lock)
        {
            _sources.RemoveAll(s => s.GetType() == source.GetType());
            _sources.Add(source);
        }
    }

    public void Remove<T>() where T : ICredentialSource
    {
        lock (_lock)
        {
            _sources.RemoveAll(s => s is T);
        }
    }

    /// <summary>The first source that handles the host, or null.</summary>
    public ICredentialSource? For(string host)
    {
        lock (_lock)
        {
            return _sources.FirstOrDefault(s => s.Handles(host));
        }
    }

    public bool Any()
    {
        lock (_lock)
        {
            return _sources.Count > 0;
        }
    }
}

/// <summary>
/// The host end of the git-credential broker: a TCP listener bound to the sandbox bridge address that
/// a sandboxed agent's git credential helper connects to. Each connection is a single request/response
/// — the guest sends <c>{token, protocol, host, path}</c>; on a covering grant (optionally gated by
/// <see cref="OnAuthorize"/>) the broker resolves a credential from a host-side source and returns it.
/// The secret is minted/read on the host and handed over only at push time — it never rests in the VM.
/// </summary>
public sealed class CredentialBrokerListener : IAsyncDisposable
{
    private readonly CredentialBrokerRegistry _grants;
    private readonly CredentialSourceRegistry _sources;
    private readonly ILogger<CredentialBrokerListener> _logger;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;

    public CredentialBrokerListener(CredentialBrokerRegistry grants, CredentialSourceRegistry sources,
        IPAddress bindAddress, int port, string advertiseHost, ILogger<CredentialBrokerListener> logger)
    {
        _grants = grants;
        _sources = sources;
        _logger = logger;
        _listener = new TcpListener(bindAddress, port);
        AdvertiseHost = advertiseHost;
    }

    /// <summary>
    /// The permission gate. Given the covering grant and the request, returns true to allow. When null,
    /// requests are allowed (the broker still enforces scope + audit). Batch 3 wires this to the UI card.
    /// </summary>
    public Func<CredentialGrant, CredentialRequest, Task<bool>>? OnAuthorize { get; set; }

    /// <summary>Invoked (token, request, allowed) after each request, for the credential audit trail.</summary>
    public Action<string, CredentialRequest, bool>? OnUse { get; set; }

    /// <summary>The host address a guest uses to reach this listener.</summary>
    public string AdvertiseHost { get; }

    /// <summary>The bound port (may be ephemeral when constructed with port 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Whether a credential source (a linked account) can serve this host — i.e. it's worth
    /// wiring the git helper into a sandbox. False means no GitHub account is linked yet.</summary>
    public bool HasSourceFor(string host) => _sources.For(host) is not null;

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        _logger.LogInformation("Credential broker on {EndPoint} (advertised to sandboxes as {Host})",
            _listener.LocalEndpoint, AdvertiseHost);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Credential broker accept failed");
                continue;
            }

            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                var req = Deserialize(line);
                if (req is null || string.IsNullOrEmpty(req.Host))
                {
                    await ReplyAsync(stream, new { error = "bad request" }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var request = new CredentialRequest(req.Protocol ?? "https", req.Host!, NormaliseRepo(req.Path), "get");
                var grant = _grants.Resolve(req.Token);
                if (grant is null || !grant.Covers(request))
                {
                    _logger.LogWarning("Credential broker denied {Host}/{Repo} (out of scope)", request.Host, request.Repo);
                    Audit(req.Token, request, allowed: false);
                    await ReplyAsync(stream, new { error = "not authorized" }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (OnAuthorize is not null && !await OnAuthorize(grant, request).ConfigureAwait(false))
                {
                    _logger.LogInformation("Credential broker: user denied {Host}/{Repo}", request.Host, request.Repo);
                    Audit(req.Token, request, allowed: false);
                    await ReplyAsync(stream, new { error = "denied" }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var source = _sources.For(request.Host);
                var credential = source is null ? null : await source.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
                if (credential is null)
                {
                    _logger.LogWarning("Credential broker: no source could resolve {Host}", request.Host);
                    Audit(req.Token, request, allowed: false);
                    await ReplyAsync(stream, new { error = "no credential" }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _logger.LogInformation("Credential broker issued a token for {Host}/{Repo}", request.Host, request.Repo);
                Audit(req.Token, request, allowed: true);
                await ReplyAsync(stream, new { username = credential.Username, password = credential.Password }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Credential broker connection failed");
            }
        }
    }

    private void Audit(string? token, CredentialRequest request, bool allowed)
    {
        if (token is not null)
        {
            OnUse?.Invoke(token, request, allowed);
        }
    }

    /// <summary>Normalises a git http path ("AdamFrisby/Agnes.git") to "owner/repo", or null if absent.</summary>
    internal static string? NormaliseRepo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var repo = path.Trim().Trim('/');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return repo.Length == 0 ? null : repo;
    }

    private static async Task ReplyAsync(Stream stream, object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(256);
        var one = new byte[1];
        while (sb.Length < 8192)
        {
            var n = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (n == 0 || one[0] == (byte)'\n')
            {
                break;
            }

            if (one[0] != (byte)'\r')
            {
                sb.Append((char)one[0]);
            }
        }

        return sb.ToString();
    }

    private static Request? Deserialize(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null
                : JsonSerializer.Deserialize<Request>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        _listener.Stop();
    }

    private sealed record Request(string? Token, string? Protocol, string? Host, string? Path, string? Operation);
}

/// <summary>The guest-side git credential helper (materialized into the VM; python is already installed).</summary>
public static class GitCredentialHelper
{
    public const string HelperHomeRelativePath = ".agnes/git-credential-agnes";

    /// <summary>
    /// Builds the guest ~/.gitconfig: point git's credential helper at the broker shim, enable
    /// <c>useHttpPath</c> so the broker learns the repo (for scoping), and (optionally) set the commit
    /// identity — the VM has no global git config, so without this <c>git commit</c> has no author.
    /// </summary>
    public static string GitConfig(string guestHome, string? userName, string? userEmail)
    {
        var helperPath = $"{guestHome.TrimEnd('/')}/{HelperHomeRelativePath}";
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userName) || !string.IsNullOrWhiteSpace(userEmail))
        {
            sb.Append("[user]\n");
            if (!string.IsNullOrWhiteSpace(userName))
            {
                sb.Append($"\tname = {userName}\n");
            }

            if (!string.IsNullOrWhiteSpace(userEmail))
            {
                sb.Append($"\temail = {userEmail}\n");
            }
        }

        sb.Append("[credential]\n");
        sb.Append($"\thelper = !python3 {helperPath}\n");
        sb.Append("\tuseHttpPath = true\n");
        return sb.ToString();
    }

    /// <summary>
    /// A git credential helper that brokers auth to the host instead of holding any secret. For a
    /// "get", it reads git's request from stdin, asks the host broker (AGNES_GIT_HOST/PORT with the
    /// session's AGNES_GIT_TOKEN) for a credential, and prints it back to git. "store"/"erase" no-op,
    /// so nothing is ever persisted in the VM.
    /// </summary>
    public const string Script = """
        import json, os, socket, struct, sys

        op = sys.argv[1] if len(sys.argv) > 1 else ""
        if op != "get":
            sys.exit(0)  # store/erase: brokered creds are never cached in the guest

        req = {}
        for line in sys.stdin:
            line = line.strip()
            if not line:
                break
            if "=" in line:
                k, v = line.split("=", 1)
                req[k] = v

        def default_gateway():
            try:
                with open("/proc/net/route") as f:
                    for line in f.readlines()[1:]:
                        p = line.split()
                        if p[1] == "00000000" and (int(p[3], 16) & 2):
                            return socket.inet_ntoa(struct.pack("<L", int(p[2], 16)))
            except Exception:
                return None
            return None

        host = os.environ.get("AGNES_GIT_HOST") or default_gateway()
        port = int(os.environ.get("AGNES_GIT_PORT", "0"))
        token = os.environ.get("AGNES_GIT_TOKEN", "")
        if not host or not port:
            sys.exit(0)  # no broker configured -> git falls through (auth simply fails)

        payload = {
            "token": token,
            "protocol": req.get("protocol", ""),
            "host": req.get("host", ""),
            "path": req.get("path", ""),
            "operation": "get",
        }
        try:
            s = socket.create_connection((host, port), timeout=120)
            s.sendall((json.dumps(payload) + "\n").encode())
            buf = b""
            while not buf.endswith(b"\n"):
                d = s.recv(4096)
                if not d:
                    break
                buf += d
            resp = json.loads(buf.decode().strip())
        except Exception:
            sys.exit(0)

        if isinstance(resp, dict) and "username" in resp and "password" in resp:
            sys.stdout.write("username=%s\n" % resp["username"])
            sys.stdout.write("password=%s\n" % resp["password"])
        # otherwise emit nothing: git treats it as "no credential available"
        """;
}
