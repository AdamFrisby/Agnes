using Agnes.Abstractions;
using Agnes.Host.Sessions;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

/// <summary>The scheduler's due-check delegates to a registered <see cref="IAutomationTrigger"/> (AC13).</summary>
public class AutomationTriggerTests
{
    private static ScheduledTask Task(int intervalSeconds)
        => new("t1", "scripted", "/tmp", "do it", intervalSeconds, Enabled: true);

    [Fact]
    public void Interval_trigger_is_due_only_after_the_interval_elapses()
    {
        var trigger = new IntervalAutomationTrigger();
        var task = Task(intervalSeconds: 60);
        var last = DateTimeOffset.UnixEpoch;

        Assert.False(trigger.IsDue(task, last, last.AddSeconds(59)));
        Assert.True(trigger.IsDue(task, last, last.AddSeconds(60)));
        Assert.True(trigger.IsDue(task, last, last.AddSeconds(120)));
        Assert.Equal("interval", trigger.Kind);
    }

    [Fact]
    public async Task Manager_uses_the_registered_trigger_to_decide_due_tasks()
    {
        var registry = new PluginRegistry<IAutomationTrigger>([new IntervalAutomationTrigger()], t => t.Kind);
        var manager = new ScheduledTaskManager(registry);
        await manager.AddAsync(new ScheduleTaskRequest("scripted", "/tmp", "hello", IntervalSeconds: 5));

        // A newly added task starts "already due" and runs on the first check.
        var due = manager.TakeDue(DateTimeOffset.UtcNow);
        Assert.Single(due);

        // Immediately checking again finds nothing due (it just ran).
        Assert.Empty(manager.TakeDue(DateTimeOffset.UtcNow));
    }

    private static ScheduledTask Cron(string expression, string timezone)
        => new("c1", "scripted", "/tmp", "do it", 0, Enabled: true, Kind: "cron", CronExpression: expression, Timezone: timezone);

    [Fact]
    public void Cron_trigger_is_due_once_now_reaches_the_next_occurrence()
    {
        var trigger = new CronAutomationTrigger();
        Assert.Equal("cron", trigger.Kind);

        // Top of every hour, UTC. Last ran at 10:00 → next occurrence is 11:00.
        var task = Cron("0 * * * *", "UTC");
        var lastRun = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.False(trigger.IsDue(task, lastRun, new DateTimeOffset(2026, 1, 1, 10, 59, 59, TimeSpan.Zero)));
        Assert.True(trigger.IsDue(task, lastRun, new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero)));
        Assert.True(trigger.IsDue(task, lastRun, new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Cron_trigger_honors_wall_clock_across_a_daylight_saving_transition()
    {
        // 09:30 daily in America/New_York. US spring-forward is 2026-03-08 02:00 (EST −05:00 → EDT −04:00).
        // The occurrence after Mar 7 must land at 09:30 *New York wall time* on Mar 8, i.e. 13:30 UTC (−04:00),
        // not 14:30 UTC (−05:00) — a fixed-offset (DST-blind) computation would fire an hour late.
        var trigger = new CronAutomationTrigger();
        var task = Cron("30 9 * * *", "America/New_York");
        var lastRun = new DateTimeOffset(2026, 3, 7, 14, 31, 0, TimeSpan.Zero); // just after Mar 7 09:30 ET (−05:00)

        Assert.False(trigger.IsDue(task, lastRun, new DateTimeOffset(2026, 3, 8, 13, 29, 0, TimeSpan.Zero)));
        Assert.True(trigger.IsDue(task, lastRun, new DateTimeOffset(2026, 3, 8, 13, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Cron_trigger_never_fires_on_a_missing_or_malformed_expression()
    {
        var trigger = new CronAutomationTrigger();
        var now = DateTimeOffset.UtcNow;

        Assert.False(trigger.IsDue(Cron("", "UTC"), DateTimeOffset.MinValue, now));
        Assert.False(trigger.IsDue(Cron("not a cron expression", "UTC"), DateTimeOffset.MinValue, now));
    }
}
