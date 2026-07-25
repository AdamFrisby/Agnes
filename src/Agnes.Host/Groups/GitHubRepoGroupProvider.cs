using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Host.Hosting;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Groups;

/// <summary>
/// The shipped group backend: a session's group is its repo, and you're "in the group" if you have <b>write
/// access</b> to that repo on GitHub. Group ids are repo keys ("github.com/owner/repo" or "owner/repo"); the
/// actual write-access check is delegated to <see cref="IGitHubRepoWriteAccess"/> so the provider's logic is
/// unit-testable without a live GitHub. Other membership models (LDAP, SSO teams) ship as separate
/// <see cref="IGroupProvider"/> plugins later.
/// </summary>
public sealed class GitHubRepoGroupProvider : IGroupProvider
{
    private readonly IGitHubRepoWriteAccess _access;

    public GitHubRepoGroupProvider(IGitHubRepoWriteAccess access) => _access = access;

    public string Id => "github-repo";

    public bool Handles(string groupId) => TryParseRepo(groupId, out _, out _);

    public async Task<bool> IsMemberAsync(GroupPrincipal principal, string groupId, CancellationToken cancellationToken = default)
    {
        // Only a GitHub-identified caller can be checked for repo write access.
        if (principal.GitHubLogin is not { Length: > 0 } login || !TryParseRepo(groupId, out var owner, out var repo))
        {
            return false;
        }

        return await _access.HasWriteAccessAsync(owner, repo, login, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a repo-key group id — "github.com/owner/repo" or bare "owner/repo" — into owner + repo.</summary>
    internal static bool TryParseRepo(string groupId, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return false;
        }

        var parts = groupId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts[0].Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            owner = parts[1];
            repo = parts[2];
            return true;
        }

        if (parts.Length == 2)
        {
            owner = parts[0];
            repo = parts[1];
            return true;
        }

        return false;
    }
}

/// <summary>Seam over the GitHub "does this user have write access to this repo?" check — kept separate so the
/// provider is testable and so the live HTTP call has one home.</summary>
public interface IGitHubRepoWriteAccess
{
    Task<bool> HasWriteAccessAsync(string owner, string repo, string login, CancellationToken cancellationToken = default);
}

/// <summary>
/// Live implementation: mints a GitHub App installation token for the repo (reusing
/// <see cref="GitHubAppCredentialSource"/> — the password of the returned credential is the installation token),
/// then queries the collaborator-permission API. <c>admin</c>/<c>maintain</c>/<c>write</c> count as membership.
/// Degrades to <c>false</c> when no GitHub App is linked or the call fails, so a PerGroup host simply grants no
/// group access rather than erroring.
/// </summary>
public sealed class GitHubApiRepoWriteAccess : IGitHubRepoWriteAccess
{
    private const string Api = "https://api.github.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly GitHubAppCredentialSource? _app;
    private readonly HttpClient _http;
    private readonly ILogger<GitHubApiRepoWriteAccess> _logger;

    public GitHubApiRepoWriteAccess(HttpClient http, ILogger<GitHubApiRepoWriteAccess> logger, GitHubAppCredentialSource? app = null)
    {
        _http = http;
        _logger = logger;
        _app = app;
    }

    public async Task<bool> HasWriteAccessAsync(string owner, string repo, string login, CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return false; // no GitHub App linked — can't check, so no group grant.
        }

        try
        {
            var cred = await _app.ResolveAsync(new CredentialRequest("https", "github.com", $"{owner}/{repo}", "get"), cancellationToken).ConfigureAwait(false);
            if (cred?.Password is not { Length: > 0 } token)
            {
                return false;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{owner}/{repo}/collaborators/{Uri.EscapeDataString(login)}/permission");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("agnes");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var permission = JsonSerializer.Deserialize<PermissionResponse>(body, Json)?.Permission;
            return permission is "admin" or "maintain" or "write";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "GitHub repo write-access check failed for {Owner}/{Repo} as {Login}", owner, repo, login);
            return false;
        }
    }

    private sealed record PermissionResponse([property: JsonPropertyName("permission")] string? Permission);
}
