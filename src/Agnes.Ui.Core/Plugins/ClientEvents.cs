using Agnes.Abstractions.Events;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Plugins;

// Client action events (see .ideas/00d-event-spine-and-ui-extensibility.md). Same taxonomy as the host:
// `Before*Event` are cancelable/mutable pre-action; `*edEvent` are observe-only facts. Kept in their own
// file (not folded into the plugin plumbing) so the client event surface grows modularly.

/// <summary>Before a notification is shown on this device. An interceptor may rewrite
/// <see cref="Notification"/> or <see cref="CancelableEvent.Cancel"/> it (do-not-disturb, reroute).</summary>
public sealed class BeforeNotificationEvent(AppNotification notification) : CancelableEvent
{
    public AppNotification Notification { get; set; } = notification;
}

/// <summary>Before a message the user composed is sent to the agent (client-side, before it leaves this
/// client). An interceptor may rewrite <see cref="Text"/> (expand a snippet, add context) or veto it.</summary>
public sealed class BeforeMessageSendEvent(string sessionId, string text) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string Text { get; set; } = text;
}

/// <summary>After a session tab is opened in the client (observe-only).</summary>
public sealed class SessionTabOpenedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After the user switches to (activates) a session tab (observe-only) — client navigation, so a
/// plugin can track focus/"currently viewing".</summary>
public sealed class SessionActivatedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>Before a session tab is closed on this client. Veto keeps the tab open (e.g. a plugin guarding
/// unsaved input). Client-only — closing a tab doesn't stop the session on the host.</summary>
public sealed class BeforeSessionCloseEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After a session tab has been closed on this client (observe-only).</summary>
public sealed class SessionClosedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>Before the user interrupts the current turn from this client (the Stop button). Veto keeps the
/// turn running (e.g. a plugin confirming interruption of a long non-idempotent operation).</summary>
public sealed class BeforeTurnCancelEvent(string sessionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After the user asked to retry/reconnect a session from this client (observe-only).</summary>
public sealed class RetryRequestedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After an attachment has been added to the composer on this client (observe-only) — a plugin can
/// react (e.g. warn on a large or sensitive file).</summary>
public sealed class AttachmentAddedEvent(string sessionId) : IAgnesEvent
{
    public string SessionId { get; } = sessionId;
}

/// <summary>After a custom plugin screen is opened as a tab (observe-only).</summary>
public sealed class CustomScreenOpenedEvent(string screenId) : IAgnesEvent
{
    public string ScreenId { get; } = screenId;
}
