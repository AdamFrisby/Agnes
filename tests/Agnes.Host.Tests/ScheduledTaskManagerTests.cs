using Agnes.Host.Sessions;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

public class ScheduledTaskManagerTests
{
    [Fact]
    public async Task Schedules_marks_due_once_and_records_the_inbox()
    {
        var manager = new ScheduledTaskManager();
        InboxRun? recorded = null;
        manager.RunRecorded += r => recorded = r;

        var task = await manager.AddAsync(new ScheduleTaskRequest("opencode", "/tmp/agnes", "audit deps", 60));
        Assert.Equal(60, task.IntervalSeconds);
        Assert.Single(manager.List());

        var now = DateTimeOffset.UtcNow;
        Assert.Single(manager.TakeDue(now));  // LastRun starts in the past → due immediately
        Assert.Empty(manager.TakeDue(now));   // just ran → not due again

        manager.Record(new InboxRun("r1", task.Id, "audit deps", "clean", now));
        Assert.Single(manager.Inbox());
        Assert.NotNull(recorded);

        await manager.RemoveAsync(task.Id);
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task Tasks_survive_a_restart_by_persisting_to_the_configured_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-sched-{Guid.NewGuid():n}.json");
        try
        {
            var first = new ScheduledTaskManager(persistPath: path);
            var a = await first.AddAsync(new ScheduleTaskRequest("opencode", "/tmp/agnes", "audit deps", 60));
            var b = await first.AddAsync(new ScheduleTaskRequest("scripted", "/tmp/agnes", "nightly lint", 120,
                Kind: "cron", CronExpression: "0 3 * * *", Timezone: "America/New_York"));

            // A fresh instance from the same file rehydrates both tasks, fields intact.
            var second = new ScheduledTaskManager(persistPath: path);
            var reloaded = second.List();
            Assert.Equal(2, reloaded.Count);
            Assert.Contains(reloaded, t => t.Id == a.Id && t.Kind == "interval" && t.IntervalSeconds == 60);
            Assert.Contains(reloaded, t => t.Id == b.Id && t.Kind == "cron"
                && t.CronExpression == "0 3 * * *" && t.Timezone == "America/New_York");

            // Removal is persisted too.
            await second.RemoveAsync(a.Id);
            Assert.Single(new ScheduledTaskManager(persistPath: path).List());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task Pausing_suppresses_due_and_resuming_re_enables_it()
    {
        var manager = new ScheduledTaskManager();
        var task = await manager.AddAsync(new ScheduleTaskRequest("scripted", "/tmp", "hello", 60));

        Assert.True(manager.SetEnabled(task.Id, enabled: false));
        Assert.Empty(manager.TakeDue(DateTimeOffset.UtcNow)); // paused → never due

        Assert.True(manager.SetEnabled(task.Id, enabled: true));
        Assert.Single(manager.TakeDue(DateTimeOffset.UtcNow)); // resumed → due again on its original schedule

        Assert.False(manager.SetEnabled("does-not-exist", enabled: true));
    }

    [Fact]
    public async Task RunNow_marks_a_task_due_immediately_without_shifting_its_schedule()
    {
        var manager = new ScheduledTaskManager();
        var now = DateTimeOffset.UtcNow;
        var task = await manager.AddAsync(new ScheduleTaskRequest("scripted", "/tmp", "hello", 3600));

        Assert.Single(manager.TakeDue(now)); // initial due
        Assert.Empty(manager.TakeDue(now));  // interval not elapsed

        Assert.True(manager.RunNow(task.Id));
        Assert.Single(manager.TakeDue(now)); // forced due immediately, out of band
        Assert.Empty(manager.TakeDue(now));  // schedule untouched — still not naturally due

        Assert.False(manager.RunNow("does-not-exist"));
    }
}
