using Agnes.Protocol;
using CronExpr = Cronos.CronExpression;

namespace Agnes.Host.Sessions;

/// <summary>
/// Decides when a scheduled task is due to run. Exposed as a plugin-point (AC13) so the scheduling
/// strategy flows through the same <see cref="Agnes.Abstractions.IPluginRegistry{TProvider}"/> as agents
/// and sandboxes: the built-in <see cref="IntervalAutomationTrigger"/> is the "every N seconds" behavior,
/// and a new kind (cron, webhook, …) can be added as a built-in or NuGet plugin without changing
/// <see cref="ScheduledTaskManager"/>. Tasks are interval-kind today; a task's trigger kind will select
/// among registered triggers once the wire model carries one.
/// </summary>
public interface IAutomationTrigger
{
    /// <summary>Stable id for this trigger kind, e.g. <c>interval</c>.</summary>
    string Kind { get; }

    /// <summary>Whether <paramref name="task"/> is due at <paramref name="now"/>, given when it last ran.</summary>
    bool IsDue(ScheduledTask task, DateTimeOffset lastRun, DateTimeOffset now);
}

/// <summary>Built-in "every N seconds since the last run" trigger — the only kind today.</summary>
public sealed class IntervalAutomationTrigger : IAutomationTrigger
{
    /// <summary>The trigger kind every task uses until the wire model carries a per-task kind.</summary>
    public const string KindId = "interval";

    public string Kind => KindId;

    public bool IsDue(ScheduledTask task, DateTimeOffset lastRun, DateTimeOffset now)
        => (now - lastRun).TotalSeconds >= task.IntervalSeconds;
}

/// <summary>
/// Cron-expression trigger (kind <c>cron</c>): a task is due once <paramref name="now"/> has reached the
/// first occurrence strictly after its last run, computed by <see href="https://github.com/HangfireIO/Cronos">Cronos</see>
/// in the task's IANA <see cref="ScheduledTask.Timezone"/> (defaulting to UTC). Delegating to Cronos keeps
/// day-of-month/day-of-week and DST-transition edge cases out of Agnes's own code. Pure: no wall clock is
/// read here — <paramref name="now"/>/<paramref name="lastRun"/> are supplied by the caller.
/// </summary>
public sealed class CronAutomationTrigger : IAutomationTrigger
{
    /// <summary>The trigger kind a task sets to schedule by cron expression.</summary>
    public const string KindId = "cron";

    public string Kind => KindId;

    public bool IsDue(ScheduledTask task, DateTimeOffset lastRun, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(task.CronExpression))
        {
            return false;
        }

        CronExpr expression;
        try
        {
            expression = CronExpr.Parse(task.CronExpression);
        }
        catch (Cronos.CronFormatException)
        {
            return false; // a malformed expression never fires rather than throwing on every tick
        }

        var next = expression.GetNextOccurrence(lastRun, ResolveTimezone(task.Timezone));
        return next.HasValue && now >= next.Value;
    }

    private static TimeZoneInfo ResolveTimezone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
