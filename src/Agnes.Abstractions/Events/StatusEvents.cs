namespace Agnes.Abstractions.Events;

// Host action event for an agent reporting its status line. See the taxonomy note in SessionEvents.cs.

/// <summary>
/// Before an agent's status line is recorded. Interceptors may rewrite <see cref="Status"/> (redaction,
/// a house style) or veto it (the report is dropped and the agent told why). The observe-only fact after
/// is the <see cref="AgentStatusEvent"/> itself on the session log.
/// </summary>
public sealed class BeforeStatusReportedEvent(string sessionId, string status) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string Status { get; set; } = status;
}
