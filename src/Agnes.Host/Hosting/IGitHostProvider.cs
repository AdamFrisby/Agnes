using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Git;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// A git forge (GitHub, GitLab, Bitbucket, …) the host knows how to work with. Exposed as a plugin-point
/// (AC13) so forge integration flows through the same <see cref="Agnes.Abstractions.IPluginRegistry{TProvider}"/>
/// as agents and sandboxes: GitHub is the built-in today, and another forge can be added as a built-in or
/// NuGet plugin. <see cref="Matches"/> identifies which forge owns a given remote host (parsed generically
/// by <see cref="GitRemote"/>), which is the seam a forge-specific feature (PR listing/checkout, richer
/// credential brokering) resolves against instead of hardcoding "github".
/// </summary>
public interface IGitHostProvider
{
    /// <summary>Stable id, e.g. <c>github</c>.</summary>
    string Id { get; }

    /// <summary>Human-friendly name.</summary>
    string DisplayName { get; }

    /// <summary>Whether this forge owns the given remote host (e.g. <c>github.com</c>).</summary>
    bool Matches(string remoteHost);

    /// <summary>
    /// Open pull/merge requests for the repository at <paramref name="remoteUrl"/>, as the forge returns them.
    /// Empty when the forge can't be reached or the remote doesn't belong to this forge. Default is empty so a
    /// forge that only participates in matching/credential routing needn't implement it.
    /// </summary>
    Task<IReadOnlyList<PullRequestInfo>> ListOpenPullRequestsAsync(string remoteUrl, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PullRequestInfo>>([]);

    /// <summary>Fetches a pull/merge request and checks it out into <paramref name="sessionWorkingDirectory"/>
    /// on a local branch, so a session can be pointed at it. Default is a typed "unsupported" result.</summary>
    Task<GitOperationResult> CheckoutPullRequestAsync(string sessionWorkingDirectory, string pullRequestId, CancellationToken cancellationToken = default)
        => Task.FromResult(new GitOperationResult(false, "This forge does not support pull-request checkout."));
}

/// <summary>
/// Built-in provider for GitHub (github.com and GitHub Enterprise subdomains). Lists open PRs via the GitHub
/// REST API (works unauthenticated for public repos; uses a token from the credential broker when one is
/// available, for higher limits and private repos) and checks a PR out via <c>git fetch origin
/// pull/&lt;id&gt;/head:pr-&lt;id&gt;</c>.
/// </summary>
public sealed class GitHubGitHostProvider : IGitHostProvider
{
    // GitHub's REST payloads are snake_case (case-insensitive so single-word keys still bind).
    private static readonly JsonSerializerOptions GitHubJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _tokenProvider;
    private readonly GitService _git;

    /// <summary>Parameterless for DI/tests that only exercise matching. A real host wires the API HttpClient
    /// and a token provider through the primary constructor.</summary>
    public GitHubGitHostProvider()
        : this(null, null, null)
    {
    }

    /// <param name="http">The HttpClient for the REST API (a shared instance in production; a stubbed handler in tests).</param>
    /// <param name="tokenProvider">Resolves an access token for (host, "owner/repo"), or null for anonymous.</param>
    /// <param name="git">The git runner used for PR checkout (defaults to a fresh <see cref="GitService"/>).</param>
    public GitHubGitHostProvider(HttpClient? http, Func<string, string, CancellationToken, Task<string?>>? tokenProvider = null, GitService? git = null)
    {
        _http = http ?? new HttpClient();
        _tokenProvider = tokenProvider;
        _git = git ?? new GitService();
    }

    public string Id => "github";
    public string DisplayName => "GitHub";

    public bool Matches(string remoteHost)
        => remoteHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           || remoteHost.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The REST endpoint listing open PRs for <c>owner/repo</c> on <paramref name="host"/>
    /// (github.com → api.github.com; a GitHub Enterprise host → <c>https://&lt;host&gt;/api/v3</c>).</summary>
    internal static string PullsApiUrl(string host, string repo)
    {
        var apiBase = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com"
            : $"https://{host}/api/v3";
        return $"{apiBase}/repos/{repo}/pulls?state=open";
    }

    /// <summary>The fetch refspec that lands PR <paramref name="pullRequestId"/> onto a local branch.</summary>
    internal static string RemoteRefspec(string pullRequestId) => $"pull/{pullRequestId}/head:{LocalBranch(pullRequestId)}";

    /// <summary>The local branch name a checked-out PR lands on.</summary>
    internal static string LocalBranch(string pullRequestId) => $"pr-{pullRequestId}";

    public async Task<IReadOnlyList<PullRequestInfo>> ListOpenPullRequestsAsync(string remoteUrl, CancellationToken cancellationToken = default)
    {
        if (!GitRemote.TryParse(remoteUrl, out var host, out var repo) || !Matches(host))
        {
            return [];
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, PullsApiUrl(host, repo));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("Agnes");

        if (_tokenProvider is not null)
        {
            var token = await _tokenProvider(host, repo, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        return ParsePullRequests(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task<GitOperationResult> CheckoutPullRequestAsync(string sessionWorkingDirectory, string pullRequestId, CancellationToken cancellationToken = default)
        => _git.FetchAndCheckoutAsync(sessionWorkingDirectory, RemoteRefspec(pullRequestId), LocalBranch(pullRequestId), cancellationToken);

    /// <summary>Parses GitHub's list-pulls response into the forge-neutral <see cref="PullRequestInfo"/>.</summary>
    internal static IReadOnlyList<PullRequestInfo> ParsePullRequests(string json)
    {
        List<PullDto>? pulls;
        try
        {
            pulls = JsonSerializer.Deserialize<List<PullDto>>(json, GitHubJson);
        }
        catch (JsonException)
        {
            return [];
        }

        if (pulls is null)
        {
            return [];
        }

        var result = new List<PullRequestInfo>(pulls.Count);
        foreach (var pull in pulls)
        {
            var id = pull.Number.ToString(CultureInfo.InvariantCulture);
            result.Add(new PullRequestInfo(
                id,
                string.IsNullOrEmpty(pull.Title) ? $"#{id}" : pull.Title,
                pull.Head?.Ref ?? string.Empty,
                pull.HtmlUrl ?? string.Empty,
                pull.User?.Login ?? string.Empty));
        }

        return result;
    }

    private sealed record PullDto(long Number, string? Title, string? HtmlUrl, PullHead? Head, PullUser? User);

    private sealed record PullHead(string? Ref);

    private sealed record PullUser(string? Login);
}
