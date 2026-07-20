using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Hosting;

/// <summary>What the Connect-GitHub flow persists: enough to mint installation tokens forever.</summary>
/// <param name="AppId">The GitHub App's numeric id (JWT issuer).</param>
/// <param name="Slug">The App's URL slug (used to build the install link).</param>
/// <param name="InstallationId">The installation on the user's account (0 until they install).</param>
/// <param name="PrivateKeyPem">The App private key (PEM) — signs the JWT. Stored 0600 on the host.</param>
public sealed record GitHubAppConfig(string AppId, string Slug, long InstallationId, string PrivateKeyPem);

/// <summary>
/// Mints short-lived, repo-scoped GitHub App installation tokens on demand — the scoped-ephemeral
/// credential the broker hands a sandbox at push time. A JWT signed with the App's private key
/// (RS256) authenticates as the App; that mints an installation access token limited to the one repo
/// and <c>contents:write</c>, expiring in ~1h. Tokens are cached per-repo until just before expiry.
/// The private key never leaves the host.
/// </summary>
public sealed class GitHubAppCredentialSource : ICredentialSource
{
    private const string Api = "https://api.github.com";
    private readonly GitHubAppConfig _config;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, GitCredential> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitHubAppCredentialSource(GitHubAppConfig config, HttpClient http)
    {
        _config = config;
        _http = http;
    }

    public bool Handles(string host) => string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase);

    public async Task<GitCredential?> ResolveAsync(CredentialRequest request, CancellationToken cancellationToken = default)
    {
        if (!Handles(request.Host) || request.Repo is null || _config.InstallationId == 0)
        {
            return null;
        }

        if (_cache.TryGetValue(request.Repo, out var cached)
            && cached.ExpiresAt is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached;
        }

        // The installation is on one account, so `repositories` takes the short repo name (no owner).
        var slash = request.Repo.IndexOf('/');
        var repoName = slash >= 0 ? request.Repo[(slash + 1)..] : request.Repo;

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{Api}/app/installations/{_config.InstallationId}/access_tokens")
        {
            Content = JsonContent.Create(new
            {
                repositories = new[] { repoName },
                permissions = new { contents = "write" },
            }),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
        httpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
        httpRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        httpRequest.Headers.UserAgent.ParseAdd("Agnes");

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var token = doc.RootElement.GetProperty("token").GetString();
        if (token is null)
        {
            return null;
        }

        var expiresAt = doc.RootElement.TryGetProperty("expires_at", out var e) && e.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow.AddMinutes(55);
        var credential = new GitCredential("x-access-token", token, expiresAt);
        _cache[request.Repo] = credential;
        return credential;
    }

    /// <summary>A short-lived (≤10 min) RS256 JWT authenticating as the App — used only to mint tokens.</summary>
    private string CreateJwt()
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iat"] = now.AddSeconds(-60).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(9).ToUnixTimeSeconds(),
            ["iss"] = _config.AppId,
        }));

        var signingInput = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_config.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
