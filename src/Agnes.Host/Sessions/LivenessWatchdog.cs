using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>
/// Reports sessions that claim to be working but have stopped getting anywhere, applying
/// <see cref="SessionLiveness"/> to every live session.
/// </summary>
/// <remarks>
/// Reports rather than acts. A wedged turn is genuinely ambiguous — the agent may yet produce something —
/// and silently restarting one would throw away work the operator can still see. Saying so in the transcript
/// turns "the UI has looked frozen for twenty minutes" into a fact with a timestamp, which is the thing that
/// was missing. Whether to intervene stays a human decision.
/// </remarks>
public sealed class LivenessWatchdog : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly SessionManager _sessions;
    private readonly LivenessOptions _options;
    private readonly ILogger<LivenessWatchdog> _logger;

    /// <summary>Sessions already reported for their current wedged spell, so one stuck turn produces one
    /// notice rather than one a minute until it moves.</summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public LivenessWatchdog(SessionManager sessions, LivenessOptions options, ILogger<LivenessWatchdog> logger)
    {
        _sessions = sessions;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Liveness watchdog tick failed");
            }
        }
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (sessionId, activity) in _sessions.LiveActivity(now))
        {
            if (SessionLiveness.Assess(activity, _options.QuietLimit) is not LivenessVerdict.Wedged)
            {
                // Moving again (or finished): re-arm so the next genuine spell is reported afresh.
                _reported.Remove(sessionId);
                continue;
            }

            if (!_reported.Add(sessionId))
            {
                continue; // already said so for this spell
            }

            var minutes = (int)activity.Quiet.TotalMinutes;
            _logger.LogWarning(
                "Session {SessionId}: a turn has been in flight for {Minutes} min with no output and no tool "
                + "call outstanding — the agent may be wedged", sessionId, minutes);

            try
            {
                await _sessions.AppendSessionNoticeAsync(
                    sessionId,
                    $"This turn has produced nothing for {minutes} minutes and has no tool call running. "
                    + "The agent may be stuck — interrupt it and send again if it doesn't move.",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not record liveness notice for session {SessionId}", sessionId);
            }
        }
    }
}
