using Agnes.Abstractions;

namespace Agnes.Host.Ops;

/// <summary>
/// Routes a submitted report to the host's selected <see cref="IBugReportSink"/> — the one named by
/// <c>Agnes:BugReports:Sink</c>, else the default chosen by which config is present (a custom endpoint when
/// configured, otherwise GitHub issues). Kept as a thin, testable seam so the hub stays a pure DTO mapper.
/// <para>
/// It also owns the owner-only, opt-in diagnostic-attachment decision: given a per-report opt-in flag and the
/// caller's id, it consults the <see cref="DiagnosticAttachmentPolicy"/> and, only when permitted, populates
/// <see cref="BugReport.DiagnosticPayload"/> from the <see cref="DiagnosticCollector"/> before routing. When
/// the collector/policy aren't wired (the default) or the caller isn't authorized/opted-in, the payload stays
/// null exactly as before — the public browser-fallback path never reaches here and never carries it.
/// </para>
/// </summary>
public sealed class BugReportRouter
{
    private readonly IPluginRegistry<IBugReportSink> _sinks;
    private readonly string _defaultSinkId;
    private readonly DiagnosticCollector? _collector;
    private readonly DiagnosticAttachmentPolicy? _policy;

    public BugReportRouter(
        IPluginRegistry<IBugReportSink> sinks,
        string defaultSinkId,
        DiagnosticCollector? collector = null,
        DiagnosticAttachmentPolicy? policy = null)
    {
        _sinks = sinks;
        _defaultSinkId = defaultSinkId;
        _collector = collector;
        _policy = policy;
    }

    /// <summary>Whether this caller may attach diagnostics (capability enabled AND caller is the owner) — the
    /// signal a client uses to decide whether to offer the "attach host diagnostics" control.</summary>
    public bool CanAttachDiagnostics(string? callerId) => _policy?.CanAttach(callerId) ?? false;

    /// <summary>Routes a report with a null diagnostic payload (the default, non-attachment path).</summary>
    public Task<BugReportResult> SubmitAsync(BugReport report, CancellationToken ct = default)
    {
        var sink = _sinks.Find(_defaultSinkId) ?? _sinks.All.FirstOrDefault();
        return sink is null
            ? Task.FromResult(new BugReportResult(false, null, "No bug-report sink is configured on this host."))
            : sink.SubmitAsync(report, ct);
    }

    /// <summary>
    /// Routes a report, attaching the host diagnostic bundle only when the caller opted in for this report AND
    /// the policy permits it (capability enabled + caller is owner). Otherwise the payload stays null.
    /// </summary>
    public Task<BugReportResult> SubmitAsync(BugReport report, bool attachDiagnostics, string? callerId, CancellationToken ct = default)
    {
        var toSubmit = report;
        if (_collector is not null && (_policy?.ShouldAttach(attachDiagnostics, callerId) ?? false))
        {
            toSubmit = report with { DiagnosticPayload = _collector.Collect() };
        }

        return SubmitAsync(toSubmit, ct);
    }
}
