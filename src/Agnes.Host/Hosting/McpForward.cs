using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// Per-session grants for the MCP forward proxy: a short-lived token maps to the set of host MCP
/// servers a sandboxed session is allowed to reach. In-memory only (sandboxed sessions don't survive
/// a host restart), mirroring the ephemeral nature of the sandboxes themselves.
/// </summary>
public sealed class McpForwardRegistry
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<McpServerInfo>> _byToken = new();

    /// <summary>Grants a session access to these servers; returns the token the shim presents.</summary>
    public string Register(IReadOnlyList<McpServerInfo> servers)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _byToken[token] = servers;
        return token;
    }

    /// <summary>Resolves a (token, server-name) pair to its spec, or null if not granted.</summary>
    public McpServerInfo? Resolve(string? token, string? server)
        => token is not null && server is not null && _byToken.TryGetValue(token, out var list)
            ? list.FirstOrDefault(s => s.Name == server)
            : null;

    public void Unregister(string? token)
    {
        if (token is not null)
        {
            _byToken.TryRemove(token, out _);
        }
    }
}

/// <summary>
/// The host end of MCP forwarding: a TCP listener (bound to the sandbox bridge address so it isn't
/// exposed on other interfaces) that a sandboxed agent's forward shim connects to. Each connection
/// sends a <c>{token, server}</c> handshake; on a valid grant the listener spawns the real host
/// stdio MCP server and byte-pumps JSON-RPC between the socket and that process — so a host MCP
/// server is reachable from inside the VM with nothing installed there.
/// </summary>
public sealed class McpForwardListener : IAsyncDisposable
{
    private readonly McpForwardRegistry _registry;
    private readonly ILogger<McpForwardListener> _logger;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;

    /// <param name="advertiseHost">The address VMs dial (the bridge gateway) — baked into their env.</param>
    public McpForwardListener(McpForwardRegistry registry, IPAddress bindAddress, int port, string advertiseHost,
        ILogger<McpForwardListener> logger)
    {
        _registry = registry;
        _logger = logger;
        _listener = new TcpListener(bindAddress, port);
        AdvertiseHost = advertiseHost;
    }

    /// <summary>The host address a guest uses to reach this listener.</summary>
    public string AdvertiseHost { get; }

    /// <summary>The bound port (may be ephemeral when constructed with port 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        _logger.LogInformation("MCP forward listener on {EndPoint} (advertised to sandboxes as {Host})",
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
                _logger.LogDebug(ex, "MCP forward accept failed");
                continue;
            }

            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            Process? backend = null;
            try
            {
                var stream = client.GetStream();
                var handshake = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                var request = Deserialize(handshake);
                var spec = _registry.Resolve(request?.Token, request?.Server);
                if (spec is null || !string.Equals(spec.Transport, "stdio", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(spec.Command))
                {
                    _logger.LogWarning("Rejected MCP forward for server '{Server}'", request?.Server);
                    return;
                }

                backend = StartBackend(spec);
                _logger.LogInformation("Forwarding MCP server '{Server}' (pid {Pid})", spec.Name, backend.Id);

                var up = Pump(stream, backend.StandardInput.BaseStream, cancellationToken);
                var down = Pump(backend.StandardOutput.BaseStream, stream, cancellationToken);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP forward connection failed");
            }
            finally
            {
                TryKill(backend);
            }
        }
    }

    private static Process StartBackend(McpServerInfo spec)
    {
        var psi = new ProcessStartInfo(spec.Command!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in spec.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (k, v) in spec.Env)
        {
            psi.Environment[k] = v;
        }

        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start MCP server '{spec.Command}'.");
    }

    // Copy src→dst, flushing each chunk so interactive JSON-RPC messages aren't buffered.
    private static async Task Pump(Stream src, Stream dst, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        int read;
        try
        {
            while ((read = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await dst.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // one side closed — the other pump completing tears the pair down
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        // The handshake is a single short line; read byte-by-byte so we don't swallow MCP bytes after it.
        var sb = new StringBuilder(128);
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

    private static Handshake? Deserialize(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null
                : JsonSerializer.Deserialize<Handshake>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            process?.Dispose();
        }
        catch
        {
            // already gone
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

    private sealed record Handshake(string Token, string Server);
}

/// <summary>The guest-side forward shim (materialized into the VM; python is already installed).</summary>
public static class McpForward
{
    public const string ShimHomeRelativePath = ".agnes/mcp-forward.py";

    /// <summary>
    /// A tiny stdio↔socket bridge the agent launches as a "local" stdio MCP server. It connects to
    /// the host forward listener (AGNES_MCP_HOST or the guest's default gateway), sends the
    /// {token, server} handshake, then pipes the agent's stdin/stdout to the socket unchanged.
    /// </summary>
    public const string ShimScript = """
        import json, os, socket, struct, sys, threading

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

        host = os.environ.get("AGNES_MCP_HOST") or default_gateway()
        port = int(os.environ.get("AGNES_MCP_PORT", "0"))
        token = os.environ.get("AGNES_MCP_TOKEN", "")
        server = sys.argv[1] if len(sys.argv) > 1 else ""
        if not host or not port:
            sys.stderr.write("agnes mcp-forward: no host/port\n"); sys.exit(1)

        s = socket.create_connection((host, port))
        s.sendall((json.dumps({"token": token, "server": server}) + "\n").encode())

        def pump(src, dst, is_sock_dst):
            try:
                while True:
                    # recv() for a socket, read1() for a pipe — both return available bytes without
                    # blocking for a full buffer (read(n) would stall interactive JSON-RPC).
                    data = src.recv(65536) if hasattr(src, "recv") else src.read1(65536)
                    if not data:
                        break
                    if is_sock_dst:
                        dst.sendall(data)
                    else:
                        dst.write(data); dst.flush()
            except Exception:
                pass
            finally:
                try:
                    if is_sock_dst:
                        dst.shutdown(socket.SHUT_WR)
                except Exception:
                    pass

        t = threading.Thread(target=pump, args=(sys.stdin.buffer, s, True), daemon=True)
        t.start()
        pump(s, sys.stdout.buffer, False)
        """;
}
