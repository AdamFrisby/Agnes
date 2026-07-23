using System.Text;

namespace Agnes.Abstractions;

/// <summary>
/// A destination a user's bug report is submitted to (GitHub issues, a self-hosted endpoint, …). A new
/// backend is a new implementation registered through the plugin registry, selected by which config is
/// present — core never special-cases a concrete sink. See <c>.ideas/ops/01-bug-reports-and-diagnostics.md</c>.
/// </summary>
public interface IBugReportSink
{
    /// <summary>Stable id this sink is registered/looked up under (e.g. "github-issue", "custom-endpoint").</summary>
    string Id { get; }

    /// <summary>Submits the report, returning a typed outcome. A browser-fallback URL or a set of likely
    /// duplicates are ordinary results the client acts on, not exceptions.</summary>
    Task<BugReportResult> SubmitAsync(BugReport report, CancellationToken ct = default);
}

/// <summary>
/// A user-authored bug report. <see cref="DiagnosticPayload"/> is the (owner-only, opt-in) host-log
/// attachment — always null for now; the sensitive attach-my-logs path is deferred (see the spec).
/// </summary>
public sealed record BugReport(
    string Title,
    string Summary,
    string? CurrentBehavior,
    string? ExpectedBehavior,
    byte[]? DiagnosticPayload);

/// <summary>
/// Outcome of a submission. On success <see cref="Url"/> is the created issue/report. On failure it may
/// still carry a prefilled browser-fallback <see cref="Url"/> (the client opens it) and/or a set of likely
/// <see cref="Duplicates"/> the client can offer to comment on instead of filing a new report.
/// </summary>
public sealed record BugReportResult(
    bool Success,
    string? Url,
    string? Error,
    IReadOnlyList<DuplicateIssue>? Duplicates = null);

/// <summary>A likely-duplicate existing issue surfaced to the user before a new one is filed.</summary>
public sealed record DuplicateIssue(int Number, string Title, string Url);

/// <summary>
/// Pure helpers for the browser-fallback path: build a plain-text issue body and a prefilled GitHub
/// "new issue" URL from a report. Shared by the host <c>GitHubIssueSink</c> and the client's ultimate
/// fallback (when the host itself is unreachable). NEVER includes <see cref="BugReport.DiagnosticPayload"/> —
/// the fallback path is public and must not leak any private diagnostic data.
/// </summary>
public static class BugReportPrefill
{
    /// <summary>Composes the report's prose fields into a Markdown issue body (no diagnostic payload).</summary>
    public static string BuildBody(BugReport report)
    {
        var sb = new StringBuilder();
        sb.Append(report.Summary);
        if (!string.IsNullOrWhiteSpace(report.CurrentBehavior))
        {
            sb.Append("\n\n**Current behavior**\n\n").Append(report.CurrentBehavior);
        }

        if (!string.IsNullOrWhiteSpace(report.ExpectedBehavior))
        {
            sb.Append("\n\n**Expected behavior**\n\n").Append(report.ExpectedBehavior);
        }

        return sb.ToString();
    }

    /// <summary>The prefilled <c>github.com/&lt;repo&gt;/issues/new</c> URL for the browser fallback.</summary>
    public static string NewIssueUrl(string repo, BugReport report)
        => $"https://github.com/{repo}/issues/new?title={Uri.EscapeDataString(report.Title)}&body={Uri.EscapeDataString(BuildBody(report))}";
}
