namespace Agnes.Abstractions;

/// <summary>
/// Optional capability on an <see cref="IAgentSession"/>: inject a message into the turn that is <em>already
/// running</em>, rather than only being able to cancel-and-restart. This is the steering seam described by
/// the pending-queue/steering spec (sessions/03): whether a message can be pushed into a live turn depends
/// entirely on what the underlying CLI's transport allows, so it is a capability a session opts into rather
/// than a method every <see cref="IAgentSession"/> must implement.
/// </summary>
/// <remarks>
/// Implemented for input-controlled sessions — i.e. those that own the CLI's raw input stream (a PTY-backed /
/// wrapped CLI) — via an <b>escape-then-message</b> primitive: write the terminal ESC byte to interrupt the
/// current generation exactly the way pressing Escape does in an interactive CLI, then type the new message.
/// A session with no controllable input stream (e.g. a plain ACP session, whose protocol has no legal way to
/// send new input mid-turn) simply does not implement this interface; the host then falls back to the
/// always-available cancel-then-resend. A user should not need to know which path fired — steering "just
/// works", via true injection where possible and cancel-then-resend otherwise.
/// </remarks>
public interface ISteerableSession
{
    /// <summary>
    /// Attempts to inject <paramref name="content"/> into the session's active turn. Returns <c>true</c> if
    /// the message was injected (the running turn keeps its work), or <c>false</c> if it could not steer
    /// (the caller then falls back to cancel-then-resend).
    /// </summary>
    Task<bool> TrySteerAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default);
}
