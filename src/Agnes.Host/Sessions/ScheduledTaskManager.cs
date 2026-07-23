using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Protocol;

namespace Agnes.Host.Sessions;

/// <summary>
/// Holds recurring background tasks and the inbox of their completed runs. The actual execution
/// is driven by <see cref="ScheduledRunner"/>; this type is the thread-safe state + due-check.
/// The due-check delegates to a registered <see cref="IAutomationTrigger"/> (AC13).
/// </summary>
public sealed class ScheduledTaskManager
{
    private readonly ConcurrentDictionary<string, Entry> _tasks = new();
    private readonly ConcurrentQueue<InboxRun> _inbox = new();
    private const int MaxInbox = 200;

    private readonly IPluginRegistry<IAutomationTrigger> _triggers;
    private readonly IEventBus _bus;

    /// <summary>The trigger registry and event bus are optional so tests can construct the manager bare; it
    /// then defaults to interval-only with an isolated bus, exactly the prior behavior.</summary>
    public ScheduledTaskManager(IPluginRegistry<IAutomationTrigger>? triggers = null, IEventBus? bus = null)
    {
        _triggers = triggers ?? new PluginRegistry<IAutomationTrigger>([new IntervalAutomationTrigger()], t => t.Kind);
        _bus = bus ?? new EventBus();
    }

    /// <summary>Raised when a run is recorded, so the host can broadcast it.</summary>
    public event Action<InboxRun>? RunRecorded;

    public async Task<ScheduledTask> AddAsync(ScheduleTaskRequest request)
    {
        if (!await _bus.AllowsAsync(
                new BeforeScheduledTaskCreateEvent(request.AdapterId, request.WorkingDirectory, request.Prompt)).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Scheduling this task was blocked by a plugin.");
        }

        var id = Guid.NewGuid().ToString("n");
        var task = new ScheduledTask(id, request.AdapterId, request.WorkingDirectory, request.Prompt,
            Math.Max(5, request.IntervalSeconds), Enabled: true);
        _tasks[id] = new Entry(task);
        await _bus.DispatchAsync(new ScheduledTaskCreatedEvent(id)).ConfigureAwait(false);
        return task;
    }

    public async Task RemoveAsync(string taskId)
    {
        if (!await _bus.AllowsAsync(new BeforeScheduledTaskRemoveEvent(taskId)).ConfigureAwait(false))
        {
            return; // a plugin kept the task
        }

        if (_tasks.TryRemove(taskId, out _))
        {
            await _bus.DispatchAsync(new ScheduledTaskRemovedEvent(taskId)).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<ScheduledTask> List() => _tasks.Values.Select(e => e.Task).ToArray();

    public IReadOnlyList<InboxRun> Inbox() => _inbox.Reverse().ToArray();

    /// <summary>Tasks a registered trigger reports as due since their last run (marks them run now).</summary>
    public IReadOnlyList<ScheduledTask> TakeDue(DateTimeOffset now)
    {
        var due = new List<ScheduledTask>();
        foreach (var entry in _tasks.Values)
        {
            if (!entry.Task.Enabled)
            {
                continue;
            }

            // Tasks are interval-kind today; resolve the trigger by that kind (fall back to interval if a
            // configured registry somehow lacks it) and ask it whether the task is due.
            var trigger = _triggers.Find(IntervalAutomationTrigger.KindId) ?? new IntervalAutomationTrigger();
            if (trigger.IsDue(entry.Task, entry.LastRun, now))
            {
                entry.LastRun = now;
                due.Add(entry.Task);
            }
        }

        return due;
    }

    public void Record(InboxRun run)
    {
        _inbox.Enqueue(run);
        while (_inbox.Count > MaxInbox)
        {
            if (!_inbox.TryDequeue(out _))
            {
                break; // drained (concurrent take) — nothing left to trim
            }
        }

        RunRecorded?.Invoke(run);
    }

    private sealed class Entry
    {
        public Entry(ScheduledTask task) => Task = task;

        public ScheduledTask Task { get; }

        // Start "already due" so a newly added task runs on the next tick.
        public DateTimeOffset LastRun { get; set; } = DateTimeOffset.MinValue;
    }
}
