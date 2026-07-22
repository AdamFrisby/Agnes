namespace Agnes.Abstractions.Events;

// Sandbox lifecycle commands — pause/resume/delete of a session's optional VM sandbox. Delete is
// destructive, so its Before* veto is the meaningful safety hook (a governance plugin can block a
// destroy). Same taxonomy as the other host-event files; one file per domain.

/// <summary>Before a session's sandbox is paused (freezes the VM). Veto leaves it running.</summary>
public sealed class BeforeSandboxPauseEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>Before a session's paused sandbox is resumed. Veto leaves it paused.</summary>
public sealed class BeforeSandboxResumeEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>Before a session's sandbox is destroyed. Veto keeps the sandbox (a plugin protecting a VM
/// with unsaved state). Destructive — this is the hook a retention/governance plugin gates on.</summary>
public sealed class BeforeSandboxDeleteEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After a session's sandbox has been destroyed (observe-only).</summary>
public sealed class SandboxDeletedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}
