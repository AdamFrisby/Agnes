namespace Agnes.Abstractions.Events;

// The inbound agent-event bridge (see .ideas/00d-event-spine-and-ui-extensibility.md).
//
// Every SessionEvent the agent produces is dispatched on the spine so plugins can react to what the agent
// does — with full typing, because SessionEvent implements IAgnesEvent: a plugin registers
// `IEventObserver<ToolCallEvent>` (or any specific kind, or the base `SessionEvent` for all of them).
// That observe path is the bulk of the value. Separately, BeforeAgentEventEvent gives a redaction hook:
// a plugin may suppress an event from being *surfaced to clients* (it is still recorded in the log, so
// history stays complete and correct). You can't "cancel" a fact the agent already produced — this only
// filters what reaches the UI.

/// <summary>Dispatched before an agent event is broadcast to clients. Cancel() suppresses the broadcast
/// (redaction) — the event is still appended to the durable log. Observe-only reaction to agent events is
/// done by registering for the SessionEvent kinds directly, not through this type.</summary>
public sealed class BeforeAgentEventEvent(string sessionId, SessionEvent @event) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public SessionEvent Event { get; } = @event;
}
