namespace Agnes.Abstractions;

/// <summary>
/// The set of "worth paging a human about" moments the host can push to a device that isn't currently
/// connected. Deliberately small and typed — a channel switches formatting/affordances on it, and a device
/// toggles each one independently. Mirrors the trigger set the channel bridges (see <c>IChannelBridge</c>)
/// already act on; when both consume the same set they can't drift.
/// </summary>
public enum NotificationTrigger
{
    /// <summary>An agent turn finished and the session is waiting (informational; no reply required).</summary>
    TurnReady,

    /// <summary>A tool-call permission request is waiting for an allow/deny decision.</summary>
    PermissionRequest,

    /// <summary>The agent asked a structured question needing a user answer.</summary>
    UserActionRequest,
}

/// <summary>
/// A single push to a single device. <see cref="ShortHint"/> is the minimized, redaction-safe body computed
/// once host-side (a tool title, a question count) — never raw tool arguments or file contents, because a
/// push is commonly shown on a lock screen. Carrying <see cref="DeviceId"/> and <see cref="SessionId"/> lets
/// a tapped interactive action name the exact device+session it should act on (see the untrusted-host guard).
/// </summary>
public sealed record NotificationPayload(
    string DeviceId,
    NotificationTrigger Trigger,
    string ShortHint,
    string SessionId);

/// <summary>
/// A host-side notification channel: the "get this to a device that isn't currently connected" abstraction,
/// sitting alongside <see cref="IAgentAdapter"/> as a normal Agnes plugin point. It composes with — does not
/// replace — the client-side <c>INotifier</c>/<c>AppNotification</c> path: a push arriving on a phone can,
/// once the app opens, feed the same in-app pipeline. Two ship in-box: a "desktop" channel wrapping the
/// existing OS-notifier path, and a "mobile-push" template where a real FCM/APNs integration is wired.
/// <para>
/// Bring-your-own credentials: each deployment supplies its own FCM/APNs credentials and the host talks to
/// the push service directly — no shared project-run relay (a standing liability and single high-value
/// target). See <c>.ideas/notifications/01-push-notifications.md</c>.
/// </para>
/// </summary>
public interface INotificationChannel
{
    /// <summary>Stable id for this channel (e.g. <c>"mobile-push"</c>, <c>"desktop"</c>), used as the
    /// plugin-registry key and the channel a device's push registration targets.</summary>
    string Id { get; }

    /// <summary>Associates a device with the channel-specific token it registered (e.g. an FCM registration
    /// token / APNs device token). Idempotent per device; a re-register replaces the prior token.</summary>
    Task RegisterAsync(string deviceId, string channelToken, CancellationToken ct = default);

    /// <summary>Delivers one minimized <paramref name="payload"/> to its target device through this channel's
    /// own transport. Purely outbound; any interactive reply comes back through the authenticated action guard,
    /// never by trusting the push payload itself.</summary>
    Task SendAsync(NotificationPayload payload, CancellationToken ct = default);
}
