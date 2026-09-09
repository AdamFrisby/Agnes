namespace Agnes.Abstractions.Events;

// Host action events for a graphical session's display. See the taxonomy note in SessionEvents.cs.

/// <summary>Who is trying to drive.</summary>
public enum DisplayInputActor
{
    Agent,
    User,
}

/// <summary>
/// Before input is injected into a session's display. An interceptor may veto (the motivating case:
/// refuse <c>type</c> while a password field has focus, or block a key chord that would escape the
/// app under test). Agent input that is vetoed is reported back as a tool error naming the reason;
/// human input that is vetoed is dropped and the client told. Frames never ride the spine.
/// </summary>
/// <param name="kind">"move", "button", "scroll" or "key".</param>
/// <param name="key">The X keysym name for a key event; null otherwise.</param>
public sealed class BeforeDisplayInputEvent(string sessionId, DisplayInputActor actor, string kind, int x, int y, string? key) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public DisplayInputActor Actor { get; } = actor;
    public string Kind { get; } = kind;
    public int X { get; } = x;
    public int Y { get; } = y;
    public string? Key { get; } = key;
}
