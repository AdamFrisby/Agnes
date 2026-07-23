using System.Collections.Concurrent;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Protocol;

namespace Agnes.Host.Sessions;

/// <summary>
/// Holds recurring background tasks and the inbox of their completed runs. The actual execution
/// is driven by <see cref="ScheduledRunner"/>; this type is the thread-safe state + due-check.
/// The due-check delegates to a registered <see cref="IAutomationTrigger"/> selected by the task's
/// <see cref="ScheduledTask.Kind"/> (AC13). Task definitions and their last-run timestamps persist to a
/// JSON file (atomic tmp-move, mirroring <see cref="Agnes.Host.Hosting.McpRegistry"/>) so a host restart
/// doesn't drop them; the inbox stays in-memory as before.
/// </summary>
public sealed class ScheduledTaskManager
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Entry> _tasks = new();
    private readonly ConcurrentQueue<InboxRun> _inbox = new();
    private const int MaxInbox = 200;

    private readonly IPluginRegistry<IAutomationTrigger> _triggers;
    private readonly IEventBus _bus;
    private readonly string? _persistPath;

    /// <summary>The trigger registry and event bus are optional so tests can construct the manager bare; it
    /// then defaults to interval-only with an isolated bus, exactly the prior behavior. <paramref name="persistPath"/>
    /// is where tasks are stored — when null/blank (the default, and how tests construct it) persistence is
    /// skipped entirely; the host passes <c>~/.agnes/scheduled-tasks.json</c>.</summary>
    public ScheduledTaskManager(
        IPluginRegistry<IAutomationTrigger>? triggers = null,
        IEventBus? bus = null,
        string? persistPath = null)
    {
        _triggers = triggers ?? new PluginRegistry<IAutomationTrigger>([new IntervalAutomationTrigger()], t => t.Kind);
        _bus = bus ?? new EventBus();
        _persistPath = string.IsNullOrWhiteSpace(persistPath) ? null : persistPath;
        Load();
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
            Math.Max(5, request.IntervalSeconds), Enabled: true,
            Kind: string.IsNullOrWhiteSpace(request.Kind) ? IntervalAutomationTrigger.KindId : request.Kind,
            CronExpression: request.CronExpression, Timezone: request.Timezone,
            TargetKind: string.IsNullOrWhiteSpace(request.TargetKind) ? "new" : request.TargetKind,
            TargetSessionId: request.TargetSessionId);
        lock (_gate)
        {
            _tasks[id] = new Entry(task);
            Save();
        }

        await _bus.DispatchAsync(new ScheduledTaskCreatedEvent(id)).ConfigureAwait(false);
        return task;
    }

    public async Task RemoveAsync(string taskId)
    {
        if (!await _bus.AllowsAsync(new BeforeScheduledTaskRemoveEvent(taskId)).ConfigureAwait(false))
        {
            return; // a plugin kept the task
        }

        bool removed;
        lock (_gate)
        {
            removed = _tasks.TryRemove(taskId, out _);
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            await _bus.DispatchAsync(new ScheduledTaskRemovedEvent(taskId)).ConfigureAwait(false);
        }
    }

    /// <summary>Pauses (<paramref name="enabled"/> false) or resumes a task; persisted. Returns false if unknown.</summary>
    public bool SetEnabled(string taskId, bool enabled)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var entry))
            {
                return false;
            }

            entry.Task = entry.Task with { Enabled = enabled };
            Save();
            return true;
        }
    }

    /// <summary>Marks a task due on the next tick regardless of its schedule, without disturbing that schedule
    /// (its last-run timestamp is untouched, so the regular cadence is unaffected — AC "Run now"). Returns
    /// false if the task is unknown.</summary>
    public bool RunNow(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var entry))
        {
            return false;
        }

        entry.ForceDue = true;
        return true;
    }

    public IReadOnlyList<ScheduledTask> List() => _tasks.Values.Select(e => e.Task).ToArray();

    public IReadOnlyList<InboxRun> Inbox() => _inbox.Reverse().ToArray();

    /// <summary>Tasks a registered trigger reports as due since their last run (marks them run now). A task
    /// flagged by <see cref="RunNow"/> is returned immediately without advancing its schedule.</summary>
    public IReadOnlyList<ScheduledTask> TakeDue(DateTimeOffset now)
    {
        var due = new List<ScheduledTask>();
        foreach (var entry in _tasks.Values)
        {
            if (!entry.Task.Enabled)
            {
                continue; // paused tasks never run, including a pending run-now request
            }

            if (entry.ForceDue)
            {
                entry.ForceDue = false;
                due.Add(entry.Task);
                continue; // out-of-band run: leave LastRun alone so the regular schedule is unaffected
            }

            // Resolve the trigger by the task's kind (fall back to interval if a configured registry somehow
            // lacks it) and ask it whether the task is due.
            var trigger = _triggers.Find(entry.Task.Kind)
                ?? _triggers.Find(IntervalAutomationTrigger.KindId)
                ?? new IntervalAutomationTrigger();
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

    // ---- persistence (atomic tmp-move JSON; skipped when no path is configured) ----

    private void Load()
    {
        if (_persistPath is null || !File.Exists(_persistPath))
        {
            return;
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<PersistedTask>>(File.ReadAllText(_persistPath));
            foreach (var r in records ?? [])
            {
                if (r.Task is { Id.Length: > 0 } task)
                {
                    _tasks[task.Id] = new Entry(task) { LastRun = r.LastRun };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt/unreadable store starts empty rather than crashing the host on boot.
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
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var records = _tasks.Values.Select(e => new PersistedTask { Task = e.Task, LastRun = e.LastRun }).ToList();
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(records));
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort; a transient write failure must not take down scheduling.
        }
    }

    private sealed class PersistedTask
    {
        public ScheduledTask? Task { get; set; }

        public DateTimeOffset LastRun { get; set; }
    }

    private sealed class Entry
    {
        public Entry(ScheduledTask task) => Task = task;

        public ScheduledTask Task { get; set; }

        // Start "already due" so a newly added task runs on the next tick.
        public DateTimeOffset LastRun { get; set; } = DateTimeOffset.MinValue;

        // Set by RunNow; cleared the next time TakeDue observes it.
        public bool ForceDue { get; set; }
    }
}
