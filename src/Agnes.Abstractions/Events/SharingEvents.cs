namespace Agnes.Abstractions.Events;

// Host action events for an agent sending the user a file. See the taxonomy note in SessionEvents.cs.

/// <summary>
/// Before a file the agent asked to send is copied and announced. Interceptors may rewrite the
/// <see cref="Caption"/> or <see cref="Cancel"/> it — a secrets scanner vetoing a file that carries a
/// credential is the motivating case. A veto is reported back to the agent as a tool error naming the
/// reason, so it can act on it rather than assume delivery. The observe-only fact after the copy is the
/// <see cref="FileSharedEvent"/> itself, which rides the spine like every other session event.
/// </summary>
/// <param name="sourcePath">The host-side absolute path of the file the agent named, already resolved to
/// lie within the session's workspace.</param>
public sealed class BeforeFileSharedEvent(string sessionId, string sourcePath, string fileName, long size, string? caption) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string SourcePath { get; } = sourcePath;
    public string FileName { get; } = fileName;
    public long Size { get; } = size;
    public string? Caption { get; set; } = caption;
}
