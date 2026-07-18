using System.Collections.Concurrent;
using Agnes.Protocol;

namespace Agnes.Host.Sessions;

/// <summary>
/// Holds recurring background tasks and the inbox of their completed runs. The actual execution
/// is driven by <see cref="ScheduledRunner"/>; this type is the thread-safe state + due-check.
/// </summary>
public sealed class ScheduledTaskManager
{
    private readonly ConcurrentDictionary<string, Entry> _tasks = new();
    private readonly ConcurrentQueue<InboxRun> _inbox = new();
    private const int MaxInbox = 200;

    /// <summary>Raised when a run is recorded, so the host can broadcast it.</summary>
    public event Action<InboxRun>? RunRecorded;

    public ScheduledTask Add(ScheduleTaskRequest request)
    {
        var id = Guid.NewGuid().ToString("n");
        var task = new ScheduledTask(id, request.AdapterId, request.WorkingDirectory, request.Prompt,
            Math.Max(5, request.IntervalSeconds), Enabled: true);
        _tasks[id] = new Entry(task);
        return task;
    }

    public void Remove(string taskId) => _tasks.TryRemove(taskId, out _);

    public IReadOnlyList<ScheduledTask> List() => _tasks.Values.Select(e => e.Task).ToArray();

    public IReadOnlyList<InboxRun> Inbox() => _inbox.Reverse().ToArray();

    /// <summary>Tasks whose interval has elapsed since their last run (marks them run now).</summary>
    public IReadOnlyList<ScheduledTask> TakeDue(DateTimeOffset now)
    {
        var due = new List<ScheduledTask>();
        foreach (var entry in _tasks.Values)
        {
            if (entry.Task.Enabled && (now - entry.LastRun).TotalSeconds >= entry.Task.IntervalSeconds)
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
        while (_inbox.Count > MaxInbox && _inbox.TryDequeue(out _))
        {
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
