namespace Agnes.Abstractions.Events;

// Session runtime commands — the operations a client issues against an already-open session (cancel a
// turn, restart the agent process, resume a stopped session). Same taxonomy as SessionEvents.cs: Before*
// are vetoable, *edEvent are observe-only facts. Kept in their own file so this domain stays cohesive and
// the core doesn't grow one monolithic events type.

/// <summary>Before the current agent turn is cancelled. Veto keeps the turn running (e.g. a plugin
/// preventing an accidental interrupt of a long, non-idempotent operation).</summary>
public sealed class BeforeSessionCancelEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>Before the agent process is restarted. Veto leaves the current agent as-is.</summary>
public sealed class BeforeAgentRestartEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After the agent process has been restarted (observe-only).</summary>
public sealed class AgentRestartedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>Before a stopped session is resumed. Veto aborts the resume (surfaces as an error).</summary>
public sealed class BeforeSessionResumeEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After a session has been resumed (observe-only).</summary>
public sealed class SessionResumedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}
