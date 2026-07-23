namespace Agnes.Abstractions;

/// <summary>
/// What happens when a user sends a message while the agent's turn is active. A per-session (or
/// account-default) setting — "what should happen when I hit send while the agent is busy" is a genuine,
/// situational preference rather than something with one right answer. See <c>sessions/03</c>.
/// </summary>
public enum SendPolicy
{
    /// <summary>Default, safest: while a turn runs, hold the message in the session's pending queue and
    /// auto-send the head of the queue when the turn ends; when idle, send immediately. Nothing is lost and
    /// nothing interrupts work already in progress.</summary>
    QueueInAgent,

    /// <summary>Cancel the in-flight turn and send the new message now — for when it supersedes what's
    /// currently happening. Implemented as the always-available cancel-then-resend fallback; true mid-turn
    /// injection (an <c>ISteerableSession</c> capability) is a deferred follow-up, since no adapter's CLI
    /// supports receiving new input mid-turn yet.</summary>
    InterruptAndSend,

    /// <summary>Always queue (even when the agent is idle) and never auto-send — only an explicit
    /// "send now" delivers a queued message.</summary>
    PendingUntilReady,
}
