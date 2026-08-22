using System.Collections.Concurrent;
using System.Text.Json;
using Agnes.Protocol;

namespace Agnes.Host.Sessions;

/// <summary>
/// Holds the goals armed on sessions and decides when one is owed a nudge. Execution lives in
/// <see cref="GoalWatcher"/>; this type is the thread-safe state plus a <b>pure</b> due-check, mirroring the
/// <see cref="ScheduledTaskManager"/>/<see cref="ScheduledRunner"/> split so the rule can be tested without
/// a clock, a session, or a background service.
/// </summary>
/// <remarks>
/// Goals persist to JSON (atomic tmp-move) so an armed goal survives a host restart — that durability is the
/// point of arming one host-side rather than looping inside a conversation, which dies with the process.
/// </remarks>
public sealed class SessionGoalManager
{
    /// <summary>Floor on the idle window. Anything shorter would nudge an agent that has merely paused
    /// between tool calls, which reads as interrupting rather than helping.</summary>
    public const int MinimumIdleSeconds = 30;

    /// <summary>Ceiling on a <b>finite</b> nudge budget, enforced regardless of what a caller asks for.
    /// A budget of <see cref="Unlimited"/> opts out of the ceiling entirely.</summary>
    public const int MaximumProds = 50;

    /// <summary>A budget of zero (or less) means "keep nudging until the goal is disarmed". Deliberately
    /// available: on an agent that stalls often, any finite budget is spent long before the work is done,
    /// and a goal that gives up early is worse than no goal. An unlimited goal is still bounded by its
    /// expiry, by the idle window between nudges, and by the user disarming it.</summary>
    public const int Unlimited = 0;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, SessionGoal> _goals = new(StringComparer.Ordinal);
    private readonly string? _persistPath;

    public SessionGoalManager(string? persistPath = null)
    {
        _persistPath = string.IsNullOrWhiteSpace(persistPath) ? null : persistPath;
        Load();
    }

    /// <summary>Raised whenever a goal is created, nudged or disarmed, so the host can broadcast it.</summary>
    public event Action<SessionGoal>? GoalChanged;

    public IReadOnlyList<SessionGoal> List() => [.. _goals.Values.OrderByDescending(g => g.CreatedAt)];

    public IReadOnlyList<SessionGoal> ListFor(string sessionId)
        => [.. _goals.Values.Where(g => string.Equals(g.SessionId, sessionId, StringComparison.Ordinal))
            .OrderByDescending(g => g.CreatedAt)];

    public SessionGoal? Get(string goalId) => _goals.TryGetValue(goalId, out var g) ? g : null;

    /// <summary>Arms a goal. A session may hold only one armed goal at a time: arming a second would give a
    /// stalled agent two different nudges and no way to tell which it was meant to follow, so the previous
    /// one is superseded rather than stacked.</summary>
    public SessionGoal Arm(ArmGoalRequest request, DateTimeOffset now)
    {
        var goal = new SessionGoal(
            Id: Guid.NewGuid().ToString("n"),
            SessionId: request.SessionId,
            Goal: request.Goal.Trim(),
            IdleSeconds: Math.Max(MinimumIdleSeconds, request.IdleSeconds),
            // <=0 is preserved as "unlimited" rather than clamped up to 1.
            MaxProds: request.MaxProds <= Unlimited ? Unlimited : Math.Clamp(request.MaxProds, 1, MaximumProds),
            ProdsUsed: 0,
            Armed: true,
            CreatedAt: now,
            ExpiresAt: request.ExpiresInSeconds is > 0 ? now.AddSeconds(request.ExpiresInSeconds.Value) : null);

        List<SessionGoal> superseded = [];
        lock (_gate)
        {
            foreach (var existing in _goals.Values.Where(g => g.Armed && g.SessionId == goal.SessionId))
            {
                var closed = existing with { Armed = false, DisarmedReason = "superseded" };
                _goals[existing.Id] = closed;
                superseded.Add(closed);
            }

            _goals[goal.Id] = goal;
            Save();
        }

        foreach (var s in superseded)
        {
            GoalChanged?.Invoke(s);
        }

        GoalChanged?.Invoke(goal);
        return goal;
    }

    /// <summary>Disarms a goal, recording why. Idempotent — disarming an already-disarmed goal keeps the
    /// original reason, so "finished" isn't later overwritten by "cancelled".</summary>
    public SessionGoal? Disarm(string goalId, string reason)
    {
        SessionGoal? updated = null;
        lock (_gate)
        {
            if (!_goals.TryGetValue(goalId, out var goal))
            {
                return null;
            }

            if (!goal.Armed)
            {
                return goal;
            }

            updated = goal with { Armed = false, DisarmedReason = reason };
            _goals[goalId] = updated;
            Save();
        }

        GoalChanged?.Invoke(updated);
        return updated;
    }

    /// <summary>Removes a goal outright (the UI's delete), armed or not.</summary>
    public bool Remove(string goalId)
    {
        lock (_gate)
        {
            if (!_goals.TryRemove(goalId, out _))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>
    /// Whether an armed goal is owed a nudge, given how its session looks right now. Pure: the caller
    /// supplies the clock and the session's state, so every branch is testable directly.
    /// </summary>
    public static GoalDecision Decide(SessionGoal goal, SessionRunState state, DateTimeOffset lastActivity, DateTimeOffset now)
    {
        if (!goal.Armed)
        {
            return GoalDecision.Nothing;
        }

        if (goal.ExpiresAt is { } expires && now >= expires)
        {
            return GoalDecision.Expired;
        }

        if (goal.MaxProds > Unlimited && goal.ProdsUsed >= goal.MaxProds)
        {
            return GoalDecision.Exhausted;
        }

        // A working session is making progress; nudging it would queue a second instruction behind whatever
        // it is already doing. Only quiet counts as stuck.
        if (state == SessionRunState.Working)
        {
            return GoalDecision.Nothing;
        }

        // A dormant session still counts: it stopped and nobody picked it up, which is exactly the case
        // this exists for — prompting it resurrects the agent.
        var idleFor = now - lastActivity;
        return idleFor.TotalSeconds >= goal.IdleSeconds ? GoalDecision.Prod : GoalDecision.Nothing;
    }

    /// <summary>Records that a nudge was delivered, advancing the budget.</summary>
    public SessionGoal? RecordProd(string goalId, DateTimeOffset now)
    {
        SessionGoal? updated = null;
        lock (_gate)
        {
            if (!_goals.TryGetValue(goalId, out var goal) || !goal.Armed)
            {
                return null;
            }

            updated = goal with { ProdsUsed = goal.ProdsUsed + 1, LastProddedAt = now };
            _goals[goalId] = updated;
            Save();
        }

        GoalChanged?.Invoke(updated);
        return updated;
    }

    // ---- persistence (atomic tmp-move, mirroring McpRegistry / ScheduledTaskManager) ----

    private void Load()
    {
        if (_persistPath is null || !File.Exists(_persistPath))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<SessionGoal[]>(File.ReadAllText(_persistPath));
            foreach (var goal in stored ?? [])
            {
                _goals[goal.Id] = goal;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file must not stop the host booting; goals are recoverable state.
        }
    }

    private void Save()
    {
        if (_persistPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_goals.Values.ToArray()));
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: an unwritable file costs durability, not correctness of the live state.
        }
    }
}

/// <summary>What the watcher should do about one armed goal on this tick.</summary>
public enum GoalDecision
{
    /// <summary>Leave it alone — the session is working, or hasn't been quiet long enough.</summary>
    Nothing,

    /// <summary>Nudge the session with the goal text.</summary>
    Prod,

    /// <summary>The nudge budget is spent; disarm and report.</summary>
    Exhausted,

    /// <summary>The goal outlived its expiry; disarm and report.</summary>
    Expired,
}
