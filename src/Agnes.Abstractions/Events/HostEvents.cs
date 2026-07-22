namespace Agnes.Abstractions.Events;

/// <summary>
/// Raised before a user prompt is sent to the agent (see <c>.ideas/00d-event-spine-and-ui-extensibility.md</c>).
/// An interceptor may rewrite <see cref="Content"/> (e.g. inject context, redact) or <see cref="CancelableEvent.Cancel"/>
/// the prompt entirely (e.g. a policy plugin blocking certain input). If canceled, the prompt is not sent.
/// </summary>
public sealed class BeforePromptEvent(string sessionId, IReadOnlyList<ContentBlock> content) : CancelableEvent
{
    public string SessionId { get; } = sessionId;

    /// <summary>The prompt content — settable so an interceptor can rewrite it before it's sent.</summary>
    public IReadOnlyList<ContentBlock> Content { get; set; } = content;
}

/// <summary>Raised (observe-only) after a session has been opened.</summary>
public sealed class SessionOpenedEvent(string sessionId, string adapterId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
    public string AdapterId { get; } = adapterId;
}
