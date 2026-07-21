using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Host config for GitHub-SSO auth (bound from <c>Agnes:Auth:GitHub</c>).</summary>
public sealed record GitHubAuthOptions
{
    public bool Enabled { get; init; }

    /// <summary>Public OAuth App client id used for the device flow (never a secret).</summary>
    public string? ClientId { get; init; }

    /// <summary>GitHub logins allowed to connect (case-insensitive).</summary>
    public string[] AllowedUsers { get; init; } = [];

    /// <summary>Allowed orgs — each is <c>org</c> (any member) or <c>org/team</c> (team member).</summary>
    public string[] AllowedOrgs { get; init; } = [];

    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(ClientId)
        && (AllowedUsers.Length > 0 || AllowedOrgs.Length > 0);
}

/// <summary>Looks identity up at GitHub with a user access token. Abstracted so tests can fake it.</summary>
public interface IGitHubUserLookup
{
    Task<string?> GetLoginAsync(string token, CancellationToken cancellationToken);
    Task<bool> IsOrgMemberAsync(string token, string org, CancellationToken cancellationToken);
    Task<bool> IsTeamMemberAsync(string token, string org, string team, string login, CancellationToken cancellationToken);
}

/// <summary>
/// Verifies a GitHub user access token (obtained by the client via the device flow) against the host's
/// allowlist. Returns the login if allowed, else null. The token is used only to check identity and is
/// never stored. The allowlist decision (login membership) is pure; org/team checks hit the API.
/// </summary>
public sealed class GitHubIdentity(IGitHubUserLookup lookup, GitHubAuthOptions options, ILogger<GitHubIdentity>? logger = null)
{
    public GitHubAuthOptions Options { get; } = options;

    public async Task<string?> VerifyAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!Options.IsUsable || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var login = await lookup.GetLoginAsync(token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        if (Options.AllowedUsers.Any(u => string.Equals(u.Trim(), login, StringComparison.OrdinalIgnoreCase)))
        {
            return login;
        }

        foreach (var spec in Options.AllowedOrgs)
        {
            var parts = spec.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var allowed = parts.Length == 1
                ? await lookup.IsOrgMemberAsync(token, parts[0], cancellationToken).ConfigureAwait(false)
                : await lookup.IsTeamMemberAsync(token, parts[0], parts[1], login, cancellationToken).ConfigureAwait(false);
            if (allowed)
            {
                return login;
            }
        }

        logger?.LogWarning("GitHub login '{Login}' is not on the allowlist — rejecting", login);
        return null;
    }
}

/// <summary>Real GitHub API lookup over HTTPS (mirrors the header setup used by <see cref="GitHubConnectFlow"/>).</summary>
public sealed class GitHubUserLookup(HttpClient http) : IGitHubUserLookup
{
    private const string Api = "https://api.github.com";

    public async Task<string?> GetLoginAsync(string token, CancellationToken cancellationToken)
    {
        using var req = Request(HttpMethod.Get, $"{Api}/user", token);
        using var res = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            return null;
        }

        var user = await res.Content.ReadFromJsonAsync<GitHubUser>(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(user?.Login) ? null : user!.Login;
    }

    public async Task<bool> IsOrgMemberAsync(string token, string org, CancellationToken cancellationToken)
    {
        // Active membership in the org (needs the read:org scope).
        using var req = Request(HttpMethod.Get, $"{Api}/user/memberships/orgs/{Uri.EscapeDataString(org)}", token);
        using var res = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            return false;
        }

        var m = await res.Content.ReadFromJsonAsync<GitHubMembership>(cancellationToken).ConfigureAwait(false);
        return string.Equals(m?.State, "active", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsTeamMemberAsync(string token, string org, string team, string login, CancellationToken cancellationToken)
    {
        using var req = Request(HttpMethod.Get,
            $"{Api}/orgs/{Uri.EscapeDataString(org)}/teams/{Uri.EscapeDataString(team)}/memberships/{Uri.EscapeDataString(login)}", token);
        using var res = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            return false;
        }

        var m = await res.Content.ReadFromJsonAsync<GitHubMembership>(cancellationToken).ConfigureAwait(false);
        return string.Equals(m?.State, "active", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("Agnes"); // GitHub rejects requests without a User-Agent.
        return req;
    }

    private sealed record GitHubUser([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubMembership([property: JsonPropertyName("state")] string? State);
}
