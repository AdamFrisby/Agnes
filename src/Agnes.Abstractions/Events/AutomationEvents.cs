namespace Agnes.Abstractions.Events;

// Automation / scheduled-task commands — create and remove recurring background tasks. A policy plugin
// can veto scheduling work against a given adapter or directory, or observe the schedule changing. Same
// taxonomy (Before* vetoable, *edEvent observe-only); one file per domain.

/// <summary>Before a recurring scheduled task is created. Interceptors may veto (creation is aborted and
/// surfaces as an error to the caller).</summary>
public sealed class BeforeScheduledTaskCreateEvent(string adapterId, string workingDirectory, string prompt) : CancelableEvent
{
    public string AdapterId { get; } = adapterId;
    public string WorkingDirectory { get; } = workingDirectory;
    public string Prompt { get; } = prompt;
}

/// <summary>After a scheduled task has been created (observe-only).</summary>
public sealed class ScheduledTaskCreatedEvent(string taskId) : IAgnesEvent
{
    public string TaskId { get; } = taskId;
}

/// <summary>Before a scheduled task is removed. Veto keeps it.</summary>
public sealed class BeforeScheduledTaskRemoveEvent(string taskId) : CancelableEvent
{
    public string TaskId { get; } = taskId;
}

/// <summary>After a scheduled task has been removed (observe-only).</summary>
public sealed class ScheduledTaskRemovedEvent(string taskId) : IAgnesEvent
{
    public string TaskId { get; } = taskId;
}
