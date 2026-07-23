using Agnes.Abstractions;

namespace Agnes.Host.Ops;

/// <summary>
/// Routes a submitted report to the host's selected <see cref="IBugReportSink"/> — the one named by
/// <c>Agnes:BugReports:Sink</c>, else the default chosen by which config is present (a custom endpoint when
/// configured, otherwise GitHub issues). Kept as a thin, testable seam so the hub stays a pure DTO mapper.
/// </summary>
public sealed class BugReportRouter
{
    private readonly IPluginRegistry<IBugReportSink> _sinks;
    private readonly string _defaultSinkId;

    public BugReportRouter(IPluginRegistry<IBugReportSink> sinks, string defaultSinkId)
    {
        _sinks = sinks;
        _defaultSinkId = defaultSinkId;
    }

    public Task<BugReportResult> SubmitAsync(BugReport report, CancellationToken ct = default)
    {
        var sink = _sinks.Find(_defaultSinkId) ?? _sinks.All.FirstOrDefault();
        return sink is null
            ? Task.FromResult(new BugReportResult(false, null, "No bug-report sink is configured on this host."))
            : sink.SubmitAsync(report, ct);
    }
}
