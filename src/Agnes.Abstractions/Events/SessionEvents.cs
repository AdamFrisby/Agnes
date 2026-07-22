namespace Agnes.Abstractions.Events;

// Host action events, grouped by domain (one file per domain — not a single monolithic events file).
//
// Taxonomy (see .ideas/00d-event-spine-and-ui-extensibility.md): `Before*Event` are CancelableEvents
// dispatched *before* an action commits — an interceptor may mutate their (settable) payload or Cancel()
// the action; each action defines what a veto does. `*edEvent` are observe-only facts dispatched *after*
// the action. The spine carries app *actions/commands*, not session *facts* — the SessionEvent log already
// is the fact stream, so it isn't re-emitted here.

/// <summary>Before a session is opened. Interceptors may rewrite the adapter/working directory or veto the
/// open (which surfaces as an error to the caller).</summary>
public sealed class BeforeSessionOpenEvent(string adapterId, string workingDirectory) : CancelableEvent
{
    public string AdapterId { get; set; } = adapterId;
    public string WorkingDirectory { get; set; } = workingDirectory;
}

/// <summary>After a session has been opened (observe-only).</summary>
public sealed class SessionOpenedEvent(string sessionId, string adapterId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
    public string AdapterId { get; } = adapterId;
}

/// <summary>Before a session is stopped/closed. Veto keeps it running.</summary>
public sealed class BeforeSessionStopEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After a session has been stopped (observe-only).</summary>
public sealed class SessionStoppedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>Before a session is forked. Interceptors may rewrite the target directory or veto (error).</summary>
public sealed class BeforeSessionForkEvent(string sourceSessionId, string targetDirectory) : CancelableEvent
{
    public string SourceSessionId { get; } = sourceSessionId;
    public string TargetDirectory { get; set; } = targetDirectory;
}

/// <summary>
/// Before a user prompt is sent to the agent. Interceptors may rewrite <see cref="Content"/> (inject
/// context, redact) or <see cref="CancelableEvent.Cancel"/> it (a policy plugin blocking input). A vetoed
/// prompt is not sent; a notice is emitted instead.
/// </summary>
public sealed class BeforePromptEvent(string sessionId, IReadOnlyList<ContentBlock> content) : CancelableEvent
{
    public string SessionId { get; } = sessionId;

    /// <summary>The prompt content — settable so an interceptor can rewrite it before it's sent.</summary>
    public IReadOnlyList<ContentBlock> Content { get; set; } = content;
}

/// <summary>Before a session's operating mode is changed. Interceptors may override the mode or veto.</summary>
public sealed class BeforeModeChangeEvent(string sessionId, string modeId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string ModeId { get; set; } = modeId;
}
