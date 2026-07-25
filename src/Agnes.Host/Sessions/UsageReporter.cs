using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>
/// Periodically logs a per-owner resource-usage summary so a sysadmin has an at-a-glance record of who is
/// consuming the shared host — the attribution the operator review asked for ("know who to blame"). Read-only;
/// logs only when there's something to report. The same snapshot is available on demand at GET /admin/usage.
/// </summary>
public sealed class UsageReporter : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly SessionManager _sessions;
    private readonly ILogger<UsageReporter> _logger;

    public UsageReporter(SessionManager sessions, ILogger<UsageReporter> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var report = _sessions.GetUsageReport();
                if (report.TotalSessions == 0)
                {
                    continue;
                }

                foreach (var owner in report.ByOwner)
                {
                    _logger.LogInformation(
                        "Usage: owner {Owner} has {Sessions} session(s), {Sandboxes} sandbox(es).",
                        owner.Owner, owner.ActiveSessions, owner.ActiveSandboxes);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — expected.
        }
    }
}
