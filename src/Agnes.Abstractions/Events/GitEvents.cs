namespace Agnes.Abstractions.Events;

// Host action events for git operations. See the taxonomy note in SessionEvents.cs.

/// <summary>Before a git commit. Interceptors may rewrite <see cref="Message"/> (e.g. append a trailer) or
/// veto the commit (which returns a failed result).</summary>
public sealed class BeforeGitCommitEvent(string sessionId, string message) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string Message { get; set; } = message;
}

/// <summary>After a successful git commit (observe-only).</summary>
public sealed class GitCommittedEvent(string sessionId, string message) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
    public string Message { get; } = message;
}
