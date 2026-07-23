using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Ops;

/// <summary>
/// Submits bug reports as GitHub issues against the configured repo. With a token: searches existing open
/// issues for a title match and surfaces them as <see cref="BugReportResult.Duplicates"/> (so the client can
/// offer "comment instead"); if none match, creates the issue via the API and returns its URL. Without a
/// token: returns a prefilled <c>issues/new</c> URL for the browser fallback (never carrying any diagnostic
/// payload). The owner-only host-log attachment is deferred, so <see cref="BugReport.DiagnosticPayload"/> is
/// always null today and never uploaded here.
/// </summary>
public sealed class GitHubIssueSink : IBugReportSink
{
    private const string Api = "https://api.github.com";

    // GitHub's REST payloads are snake_case (case-insensitive so single-word keys match too).
    private static readonly JsonSerializerOptions GitHubJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _repo;
    private readonly string? _token;
    private readonly ILogger<GitHubIssueSink> _logger;

    public GitHubIssueSink(HttpClient http, string repo, string? token, ILogger<GitHubIssueSink> logger)
    {
        _http = http;
        _repo = repo.Trim('/');
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _logger = logger;
    }

    public string Id => "github-issue";

    public async Task<BugReportResult> SubmitAsync(BugReport report, CancellationToken ct = default)
    {
        // No token → no API access: hand back a prefilled public new-issue URL for the browser to open.
        // The fallback URL carries only the prose fields, never any diagnostic payload.
        if (_token is null)
        {
            return new BugReportResult(Success: false, Url: BugReportPrefill.NewIssueUrl(_repo, report), Error: null);
        }

        try
        {
            var duplicates = await SearchDuplicatesAsync(report.Title, ct).ConfigureAwait(false);
            if (duplicates.Count > 0)
            {
                // Let the user comment on an existing issue instead of opening another — no new issue yet.
                return new BugReportResult(Success: false, Url: null, Error: null, Duplicates: duplicates);
            }

            var created = await CreateIssueAsync(report, ct).ConfigureAwait(false);
            return created is null
                ? new BugReportResult(false, null, "GitHub did not return the created issue.")
                : new BugReportResult(true, created.HtmlUrl, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "GitHub issue submission failed; offering the browser fallback.");
            // Degrade to the public browser fallback rather than losing the user's report.
            return new BugReportResult(false, BugReportPrefill.NewIssueUrl(_repo, report), ex.Message);
        }
    }

    private async Task<IReadOnlyList<DuplicateIssue>> SearchDuplicatesAsync(string title, CancellationToken ct)
    {
        var query = $"repo:{_repo} is:issue is:open in:title {title}";
        using var request = Authorized(HttpMethod.Get, $"{Api}/search/issues?q={Uri.EscapeDataString(query)}");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(GitHubJson, ct).ConfigureAwait(false);
        return result?.Items
            ?.Where(i => !string.IsNullOrEmpty(i.HtmlUrl))
            .Select(i => new DuplicateIssue(i.Number, i.Title ?? string.Empty, i.HtmlUrl!))
            .ToArray() ?? [];
    }

    private async Task<CreateIssueResponse?> CreateIssueAsync(BugReport report, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, $"{Api}/repos/{_repo}/issues");
        request.Content = JsonContent.Create(new CreateIssueRequest(report.Title, BugReportPrefill.BuildBody(report)), options: GitHubJson);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateIssueResponse>(GitHubJson, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("Agnes");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private sealed record SearchResponse(IReadOnlyList<SearchItem>? Items);

    private sealed record SearchItem(int Number, string? Title, string? HtmlUrl);

    private sealed record CreateIssueRequest(string Title, string Body);

    private sealed record CreateIssueResponse(int Number, string? HtmlUrl);
}
