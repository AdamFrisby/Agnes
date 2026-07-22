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
    public void Manager_uses_the_registered_trigger_to_decide_due_tasks()
    {
        var registry = new PluginRegistry<IAutomationTrigger>([new IntervalAutomationTrigger()], t => t.Kind);
        var manager = new ScheduledTaskManager(registry);
        manager.Add(new ScheduleTaskRequest("scripted", "/tmp", "hello", IntervalSeconds: 5));

        // A newly added task starts "already due" and runs on the first check.
        var due = manager.TakeDue(DateTimeOffset.UtcNow);
        Assert.Single(due);

        // Immediately checking again finds nothing due (it just ran).
        Assert.Empty(manager.TakeDue(DateTimeOffset.UtcNow));
    }
}
