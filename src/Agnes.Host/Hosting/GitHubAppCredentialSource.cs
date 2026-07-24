using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Hosting;

/// <summary>A linked GitHub account — enough to mint installation tokens for it forever.</summary>
/// <param name="AppId">The GitHub App's numeric id (JWT issuer).</param>
/// <param name="Slug">The App's URL slug (used to build the install link).</param>
/// <param name="InstallationId">The installation on the account (0 until the user installs).</param>
/// <param name="PrivateKeyPem">The App private key (PEM) — signs the JWT. Stored 0600 on the host.</param>
/// <param name="Account">The account login this App belongs to (e.g. "AdamFrisby" or a work org) — used to route by repo owner.</param>
public sealed record GitHubAppConfig(string AppId, string Slug, long InstallationId, string PrivateKeyPem, string Account = "");

/// <summary>
/// Mints short-lived, repo-scoped GitHub App installation tokens on demand across one or more linked
/// accounts — the scoped-ephemeral credential the broker hands a sandbox at push time. It routes by
/// the repo's owner: a push to <c>work-org/svc</c> mints from the App linked to "work-org", a push to
/// <c>me/app</c> from the App linked to "me", so different projects push as different accounts with no
/// per-session wiring. A JWT signed with the account's App key mints a token limited to the one repo +
/// <c>contents:write</c>, ~1h TTL, cached per (account, repo). Private keys never leave the host.
/// </summary>
public sealed class GitHubAppCredentialSource : ICredentialSource
{
    private const string Api = "https://api.github.com";
    private readonly Func<IReadOnlyList<GitHubAppConfig>> _accounts;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, GitCredential> _cache = new(StringComparer.OrdinalIgnoreCase);

    // GitHub's snake_case REST payload (installation access token).
    private static readonly JsonSerializerOptions GitHubJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record InstallationToken(string? Token, DateTimeOffset? ExpiresAt);

    public GitHubAppCredentialSource(Func<IReadOnlyList<GitHubAppConfig>> accounts, HttpClient http)
    {
        _accounts = accounts;
        _http = http;
    }

    public bool Handles(string host) => string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase);

    public async Task<GitCredential?> ResolveAsync(CredentialRequest request, CancellationToken cancellationToken = default)
    {
        if (!Handles(request.Host) || request.Repo is null)
        {
            return null;
        }

        var installed = _accounts().Where(a => a.InstallationId != 0).ToList();
        if (installed.Count == 0)
        {
            return null;
        }

        var slash = request.Repo.IndexOf('/');
        var owner = slash >= 0 ? request.Repo[..slash] : string.Empty;
        var repoName = slash >= 0 ? request.Repo[(slash + 1)..] : request.Repo;

        // If the request is pinned to a specific linked account (the session's project.CredentialAccount),
        // mint ONLY against that account — deny rather than silently fall back, so a session can't reach a
        // different tenant's account. Otherwise route by repo owner, falling back to the first linked account.
        GitHubAppConfig? config;
        if (request.Account is { Length: > 0 } pinned)
        {
            config = installed.FirstOrDefault(a => string.Equals(a.Account, pinned, StringComparison.OrdinalIgnoreCase));
            if (config is null)
            {
                return null; // pinned account isn't linked/installed — refuse rather than substitute another.
            }
        }
        else
        {
            config = installed.FirstOrDefault(a => string.Equals(a.Account, owner, StringComparison.OrdinalIgnoreCase)) ?? installed[0];
        }

        var cacheKey = $"{config.AppId}/{request.Repo}";
        if (_cache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached;
        }

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{Api}/app/installations/{config.InstallationId}/access_tokens")
        {
            Content = JsonContent.Create(new
            {
                repositories = new[] { repoName },
                permissions = new { contents = "write" },
            }),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt(config));
        httpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
        httpRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        httpRequest.Headers.UserAgent.ParseAdd("Agnes");

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (JsonSerializer.Deserialize<InstallationToken>(body, GitHubJson) is not { Token: { } token } installation)
        {
            return null;
        }

        // GitHub returns a ~1h expiry; fall back to a conservative 55 min if it's absent.
        var expiresAt = installation.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(55);
        var credential = new GitCredential("x-access-token", token, expiresAt);
        _cache[cacheKey] = credential;
        return credential;
    }

    /// <summary>A short-lived (≤10 min) RS256 JWT authenticating as the account's App — mints tokens only.</summary>
    private static string CreateJwt(GitHubAppConfig config)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iat"] = now.AddSeconds(-60).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(9).ToUnixTimeSeconds(),
            ["iss"] = config.AppId,
        }));

        var signingInput = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(config.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
