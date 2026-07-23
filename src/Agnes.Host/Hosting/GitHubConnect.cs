using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Persists the linked GitHub accounts (one App each) 0600 on the host — supports several.</summary>
public sealed class GitHubAppStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private List<GitHubAppConfig> _apps = new();

    public GitHubAppStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "github-app.json");
        Load();
    }

    public IReadOnlyList<GitHubAppConfig> List()
    {
        lock (_gate)
        {
            return _apps.ToArray();
        }
    }

    public GitHubAppConfig? Get(string account)
    {
        lock (_gate)
        {
            return _apps.FirstOrDefault(a => string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The most-recently-created App still awaiting installation (installation id 0).</summary>
    public GitHubAppConfig? PendingInstall()
    {
        lock (_gate)
        {
            return _apps.LastOrDefault(a => a.InstallationId == 0);
        }
    }

    /// <summary>Inserts or updates an account by App id.</summary>
    public void Save(GitHubAppConfig config)
    {
        lock (_gate)
        {
            var index = _apps.FindIndex(a => a.AppId == config.AppId);
            if (index >= 0)
            {
                _apps[index] = config;
            }
            else
            {
                _apps.Add(config);
            }

            Persist();
        }
    }

    public bool Remove(string account)
    {
        lock (_gate)
        {
            var removed = _apps.RemoveAll(a => string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path).TrimStart();
                _apps = text.StartsWith('[')
                    ? JsonSerializer.Deserialize<List<GitHubAppConfig>>(text, Options) ?? new List<GitHubAppConfig>()
                    // Migrate the earlier single-App format to a one-element list.
                    : JsonSerializer.Deserialize<GitHubAppConfig>(text, Options) is { } single ? new List<GitHubAppConfig> { single } : new List<GitHubAppConfig>();
            }
        }
        catch
        {
            _apps = new List<GitHubAppConfig>();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_apps, Options));
            File.Move(tmp, _path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600 — holds private keys
                }
                catch
                {
                    // best effort
                }
            }
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>Connection state surfaced to the UI.</summary>
public sealed record GitHubConnectStatus(string State, string? Slug, bool Installed, string? Account);

/// <summary>
/// Drives the GitHub App Manifest flow so the user links GitHub in two clicks — no developer portal,
/// no PAT, no key handling. We hand GitHub a pre-filled app manifest (contents:write, metadata:read);
/// on approval GitHub calls us back with a temporary code, which we exchange for the App id + private
/// key (stored 0600); we then send the user to install it on their repos. Once installed, the minting
/// <see cref="GitHubAppCredentialSource"/> is registered live.
/// </summary>
public sealed class GitHubConnectFlow
{
    private const string Api = "https://api.github.com";
    private readonly GitHubAppStore _store;
    private readonly CredentialSourceRegistry _sources;
    private readonly HttpClient _http;
    private readonly ILogger<GitHubConnectFlow> _logger;
    private readonly ConcurrentDictionary<string, string> _pending = new(); // state -> baseUrl

    public GitHubConnectFlow(GitHubAppStore store, CredentialSourceRegistry sources, HttpClient http, ILogger<GitHubConnectFlow> logger)
    {
        _store = store;
        _sources = sources;
        _http = http;
        _logger = logger;

        // One minting source spans every linked account (it reads the store live), so accounts added
        // later by a subsequent Connect are picked up without re-registering.
        _sources.Set(new GitHubAppCredentialSource(() => _store.List(), _http));
    }

    public GitHubConnectStatus Status()
    {
        var apps = _store.List();
        var installed = apps.Where(a => a.InstallationId > 0).ToArray();
        if (installed.Length > 0)
        {
            return new GitHubConnectStatus("connected", installed[0].Slug, true, string.Join(", ", installed.Select(a => a.Account)));
        }

        return apps.Count > 0
            ? new GitHubConnectStatus("app-created", apps[^1].Slug, false, null)
            : new GitHubConnectStatus("not-connected", null, false, null);
    }

    /// <summary>Begins a connect: returns the loopback URL the desktop opens in a browser.</summary>
    public string BeginConnect(string baseUrl)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _pending[state] = baseUrl.TrimEnd('/');
        return $"{baseUrl.TrimEnd('/')}/credentials/github/start?state={state}";
    }

    /// <summary>The auto-submitting form that POSTs the app manifest to GitHub, or null on a bad state.</summary>
    public string? StartPage(string? state)
    {
        if (state is null || !_pending.TryGetValue(state, out var baseUrl))
        {
            return null;
        }

        var manifest = BuildManifest(baseUrl);
        var encoded = WebUtility.HtmlEncode(manifest);
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Connecting Agnes to GitHub…</title></head>
            <body style="font-family:system-ui;padding:2rem">
            <p>Redirecting you to GitHub to create the Agnes app…</p>
            <form id="f" action="https://github.com/settings/apps/new?state={{state}}" method="post">
              <input type="hidden" name="manifest" value="{{encoded}}">
              <noscript><button type="submit">Continue to GitHub</button></noscript>
            </form>
            <script>document.getElementById('f').submit();</script>
            </body></html>
            """;
    }

    /// <summary>The app-manifest GitHub creates on the user's behalf (they just approve it).</summary>
    internal static string BuildManifest(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = $"Agnes ({Environment.MachineName})",
            ["url"] = b,
            ["redirect_url"] = $"{b}/credentials/github/callback",   // where GitHub returns the creation code
            ["setup_url"] = $"{b}/credentials/github/callback",       // where GitHub returns after install (installation_id)
            ["public"] = false,
            ["default_permissions"] = new Dictionary<string, string> { ["contents"] = "write", ["metadata"] = "read" },
            ["default_events"] = Array.Empty<string>(),
        });
    }

    /// <summary>
    /// The GitHub callback — one endpoint for both legs. With <c>code</c>: exchange it for the App
    /// (id + private key), store it, and send the user to install. With <c>installation_id</c>: record
    /// the installation and register the minting source. Returns an HTML page to show the user.
    /// </summary>
    public async Task<string> HandleCallbackAsync(string? code, string? state, string? installationId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(installationId) && long.TryParse(installationId, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            var pending = _store.PendingInstall();
            if (pending is null)
            {
                return Page("Something went wrong", "No app was created before installation. Please try Connect again.");
            }

            _store.Save(pending with { InstallationId = id }); // the live source picks this up
            _logger.LogInformation("GitHub App installed for {Account} (installation {Id}).", pending.Account, id);
            return Page("GitHub connected ✓", $"Agnes can now push as {pending.Account} with short-lived, repo-scoped tokens. You can close this tab.");
        }

        if (string.IsNullOrEmpty(code) || state is null || !_pending.TryRemove(state, out var baseUrl))
        {
            return Page("Couldn't connect", "The link was invalid or expired. Please start Connect GitHub again.");
        }

        var converted = await ConvertManifestAsync(code, cancellationToken).ConfigureAwait(false);
        if (converted is null)
        {
            return Page("Couldn't connect", "GitHub didn't return the app details. Please try again.");
        }

        _store.Save(converted);
        _logger.LogInformation("GitHub App '{Slug}' created (id {AppId}); prompting install.", converted.Slug, converted.AppId);

        // Send the user straight to the install page; GitHub returns to this same callback with installation_id.
        var installUrl = $"https://github.com/apps/{converted.Slug}/installations/new";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Install Agnes…</title></head>
            <body style="font-family:system-ui;padding:2rem">
            <p>App created. Redirecting you to choose which repositories Agnes may push to…</p>
            <script>location.href={{JsonSerializer.Serialize(installUrl)}};</script>
            <p><a href="{{installUrl}}">Continue to install</a></p>
            </body></html>
            """;
    }

    private async Task<GitHubAppConfig?> ConvertManifestAsync(string code, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/app-manifests/{Uri.EscapeDataString(code)}/conversions");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("Agnes");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Manifest conversion failed: {Status}", response.StatusCode);
            return null;
        }

        return ParseConversion(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    // Shared options for GitHub's snake_case REST payloads (case-insensitive so single-word keys match too).
    private static readonly JsonSerializerOptions GitHubJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record AppManifestConversion(long Id, string? Slug, string? Pem, AppOwner? Owner);

    private sealed record AppOwner(string? Login);

    /// <summary>Parses GitHub's app-manifest conversion response into the fields we persist.</summary>
    internal static GitHubAppConfig? ParseConversion(string json)
    {
        AppManifestConversion? conversion;
        try
        {
            conversion = JsonSerializer.Deserialize<AppManifestConversion>(json, GitHubJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (conversion is not { Slug: { } slug, Pem: { } pem })
        {
            return null;
        }

        var appId = conversion.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new GitHubAppConfig(appId, slug, 0, pem, conversion.Owner?.Login ?? string.Empty);
    }

    private static string Page(string title, string body) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>{{title}}</title></head>
        <body style="font-family:system-ui;padding:2rem">
        <h2>{{title}}</h2><p>{{body}}</p>
        </body></html>
        """;
}
