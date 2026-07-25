using Agnes.Host.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Events;

/// <summary>
/// Enforces the transcript-retention window (Agnes:Security:TranscriptRetentionDays) with a daily sweep that
/// prunes event-log entries older than the cutoff. Disabled (never runs) when retention is 0. Runs one sweep
/// shortly after startup, then daily. Only transcript content ages out — session catalogue rows are kept.
/// </summary>
public sealed class TranscriptRetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IEventStore _store;
    private readonly SessionSecurityOptions _security;
    private readonly ILogger<TranscriptRetentionService> _logger;

    public TranscriptRetentionService(IEventStore store, SessionSecurityOptions security, ILogger<TranscriptRetentionService> logger)
    {
        _store = store;
        _security = security;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_security.TranscriptRetentionDays <= 0)
        {
            return; // retention disabled — keep everything.
        }

        var window = TimeSpan.FromDays(_security.TranscriptRetentionDays);
        try
        {
            // A short initial delay so startup/restore isn't competing with a bulk delete.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await PruneOnceAsync(window, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — expected.
        }
    }

    private async Task PruneOnceAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            var removed = await _store.PruneEventsBeforeAsync(cutoff, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("Transcript retention: pruned {Count} event(s) older than {Days} day(s).",
                    removed, _security.TranscriptRetentionDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Transcript retention sweep failed; will retry on the next cycle.");
        }
    }
}
