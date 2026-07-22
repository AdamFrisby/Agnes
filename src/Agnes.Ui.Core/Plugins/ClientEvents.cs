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

/// <summary>After a custom plugin screen is opened as a tab (observe-only).</summary>
public sealed class CustomScreenOpenedEvent(string screenId) : IAgnesEvent
{
    public string ScreenId { get; } = screenId;
}
