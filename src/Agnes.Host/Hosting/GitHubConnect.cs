using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Persists the linked GitHub App (id, slug, installation, private key) 0600 on the host.</summary>
public sealed class GitHubAppStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public GitHubAppStore(string? path = null)
        => _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "github-app.json");

    public GitHubAppConfig? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<GitHubAppConfig>(File.ReadAllText(_path), Options) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(GitHubAppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(config, Options));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600 — holds the private key
            }
            catch
            {
                // best effort
            }
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // already gone
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

        // Re-register the minting source on startup if an App is already linked + installed.
        if (_store.Load() is { InstallationId: > 0 } existing && !string.IsNullOrWhiteSpace(existing.PrivateKeyPem))
        {
            _sources.Set(new GitHubAppCredentialSource(existing, _http));
        }
    }

    public GitHubConnectStatus Status()
    {
        var app = _store.Load();
        if (app is null)
        {
            return new GitHubConnectStatus("not-connected", null, false, null);
        }

        return new GitHubConnectStatus(app.InstallationId > 0 ? "connected" : "app-created", app.Slug, app.InstallationId > 0, null);
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
        if (!string.IsNullOrEmpty(installationId) && long.TryParse(installationId, out var id))
        {
            var app = _store.Load();
            if (app is null)
            {
                return Page("Something went wrong", "No app was created before installation. Please try Connect again.");
            }

            var updated = app with { InstallationId = id };
            _store.Save(updated);
            _sources.Set(new GitHubAppCredentialSource(updated, _http));
            _logger.LogInformation("GitHub App installed (installation {Id}); credential source is live.", id);
            return Page("GitHub connected ✓", "Agnes can now mint short-lived, repo-scoped push tokens. You can close this tab.");
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

    /// <summary>Parses GitHub's app-manifest conversion response into the fields we persist.</summary>
    internal static GitHubAppConfig? ParseConversion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var appId = root.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var slug = root.GetProperty("slug").GetString();
            var pem = root.GetProperty("pem").GetString();
            return slug is null || pem is null ? null : new GitHubAppConfig(appId, slug, 0, pem);
        }
        catch
        {
            return null;
        }
    }

    private static string Page(string title, string body) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>{{title}}</title></head>
        <body style="font-family:system-ui;padding:2rem">
        <h2>{{title}}</h2><p>{{body}}</p>
        </body></html>
        """;
}
