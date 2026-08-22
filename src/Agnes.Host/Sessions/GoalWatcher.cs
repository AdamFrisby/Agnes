using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>
/// Nudges sessions that have gone quiet while a goal is still armed on them. Execution half of
/// <see cref="SessionGoalManager"/>, the way <see cref="ScheduledRunner"/> is to
/// <see cref="ScheduledTaskManager"/>.
/// </summary>
/// <remarks>
/// Idleness is measured from the session's head sequence rather than a wall-clock field on the session:
/// the head only moves when the session actually emits an event, so "head unchanged for N seconds" is
/// exactly "the agent has done nothing for N seconds", with no extra bookkeeping on the hot event path.
///
/// A session first seen by this watcher starts its idle clock now, not at its last event. That means a
/// restart grants every armed goal one fresh idle window before it can nudge — deliberate, because the
/// alternative is a host restart firing a burst of nudges at every dormant session at once.
/// </remarks>
public sealed class GoalWatcher : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly SessionGoalManager _goals;
    private readonly SessionManager _sessions;
    private readonly IHubContext<AgnesHub, IAgnesClient> _hub;
    private readonly ILogger<GoalWatcher> _logger;

    /// <summary>Per-session "head sequence, and when we first saw it at that value".</summary>
    private readonly Dictionary<string, (long Head, DateTimeOffset Since)> _activity = new(StringComparer.Ordinal);

    public GoalWatcher(
        SessionGoalManager goals,
        SessionManager sessions,
        IHubContext<AgnesHub, IAgnesClient> hub,
        ILogger<GoalWatcher> logger)
    {
        _goals = goals;
        _sessions = sessions;
        _hub = hub;
        _logger = logger;
        _goals.GoalChanged += goal => _ = _hub.Clients.All.OnGoalChanged(goal);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                _logger.LogError(ex, "Goal watcher tick failed");
            }
        }
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var armed = _goals.List().Where(g => g.Armed).ToList();
        if (armed.Count == 0)
        {
            return;
        }

        var summaries = (await _sessions.ListSessionSummariesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.SessionId, s => s, StringComparer.Ordinal);

        foreach (var goal in armed)
        {
            if (!summaries.TryGetValue(goal.SessionId, out var summary))
            {
                // The session is gone; a goal pointing at nothing can never be met.
                _goals.Disarm(goal.Id, "session no longer exists");
                continue;
            }

            var idleSince = TrackActivity(goal.SessionId, summary.HeadSequence, now);

            // A goal that has never nudged is a fresh instruction: if the session is already stopped, the
            // user meant "get on with it", not "wait one idle window first". Later nudges obey the window.
            var effectiveIdleSince = goal.ProdsUsed == 0 && goal.LastProddedAt is null
                ? idleSince - TimeSpan.FromSeconds(goal.IdleSeconds)
                : idleSince;

            switch (SessionGoalManager.Decide(goal, summary.State, effectiveIdleSince, now))
            {
                case GoalDecision.Expired:
                    _goals.Disarm(goal.Id, "expired");
                    await NoteAsync(goal, "The goal expired before it was met.", cancellationToken).ConfigureAwait(false);
                    break;

                case GoalDecision.Exhausted:
                    _goals.Disarm(goal.Id, "nudge budget spent");
                    await NoteAsync(goal,
                        $"Nudged {goal.ProdsUsed} time(s) without the goal being completed — no longer nudging.",
                        cancellationToken).ConfigureAwait(false);
                    break;

                case GoalDecision.Prod:
                    await ProdAsync(goal, now, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Updates the head-sequence watermark and returns when this session last changed it.</summary>
    private DateTimeOffset TrackActivity(string sessionId, long head, DateTimeOffset now)
    {
        if (_activity.TryGetValue(sessionId, out var seen) && seen.Head == head)
        {
            return seen.Since;
        }

        _activity[sessionId] = (head, now);
        return now;
    }

    private async Task ProdAsync(SessionGoal goal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Record first: if the send throws, the budget is still spent, so a session that reliably fails to
        // accept prompts can't be nudged forever.
        var updated = _goals.RecordProd(goal.Id, now);
        if (updated is null)
        {
            return; // disarmed between the decision and here
        }

        _logger.LogInformation(
            "Session {SessionId}: idle past {IdleSeconds}s with a goal armed; nudging ({Used}/{Max})",
            goal.SessionId, goal.IdleSeconds, updated.ProdsUsed,
            updated.MaxProds > SessionGoalManager.Unlimited ? updated.MaxProds.ToString() : "unlimited");

        var text =
            $"You appear to have stopped without completing this goal:\n\n{goal.Goal}\n\n"
            + "Continue working towards it. If it is already complete, or you are genuinely blocked and "
            + "further attempts cannot help, disarm the goal instead of continuing.";

        try
        {
            // Submit rather than prompt: the session may have started a turn since the decision above, and
            // queueing behind it is right where starting a second concurrent turn would not be.
            await _sessions.SubmitAsync(goal.SessionId, [new TextContent(text)]).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not nudge session {SessionId} for goal {GoalId}", goal.SessionId, goal.Id);
        }
    }

    /// <summary>Writes a visible line into the session so a goal ending is never silent.</summary>
    private async Task NoteAsync(SessionGoal goal, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _sessions.AppendSessionNoticeAsync(goal.SessionId, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record goal notice for session {SessionId}", goal.SessionId);
        }
    }
}
