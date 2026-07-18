using Agnes.Host.Sessions;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

public class ScheduledTaskManagerTests
{
    [Fact]
    public void Schedules_marks_due_once_and_records_the_inbox()
    {
        var manager = new ScheduledTaskManager();
        InboxRun? recorded = null;
        manager.RunRecorded += r => recorded = r;

        var task = manager.Add(new ScheduleTaskRequest("opencode", "/tmp/agnes", "audit deps", 60));
        Assert.Equal(60, task.IntervalSeconds);
        Assert.Single(manager.List());

        var now = DateTimeOffset.UtcNow;
        Assert.Single(manager.TakeDue(now));  // LastRun starts in the past → due immediately
        Assert.Empty(manager.TakeDue(now));   // just ran → not due again

        manager.Record(new InboxRun("r1", task.Id, "audit deps", "clean", now));
        Assert.Single(manager.Inbox());
        Assert.NotNull(recorded);

        manager.Remove(task.Id);
        Assert.Empty(manager.List());
    }
}
