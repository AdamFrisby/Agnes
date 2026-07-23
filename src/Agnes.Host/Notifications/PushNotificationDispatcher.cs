using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Notifications;

/// <summary>
/// The host-driven push sender: watches the event spine and, when a session crosses a notification trigger
/// (turn ready, permission requested, user-action/question requested), fans a minimized
/// <see cref="NotificationPayload"/> out to every eligible registered device through its channel. This is the
/// exact mirror of <c>ChannelBridgeNotifier</c> — the host is already the one place that observes every
/// <see cref="SessionEvent"/>, so firing sends from here needs no new process, polling loop, or risk of a
/// separate notifier drifting out of sync about session state.
/// <para>
/// It observes <see cref="BeforeAgentEventEvent"/> because that is the single spine event pairing a
/// <see cref="SessionEvent"/> with its session id. It is a pure observer: it never cancels the event or
/// changes the action's outcome (a channel send failing is swallowed per-device), so it can't affect whether
/// the underlying event reaches clients, and it is independent of any other delivery path (bridges, in-app).
/// Adding a new <see cref="INotificationChannel"/> needs no change here — it enumerates the registry.
/// </para>
/// <para>
/// Eligibility per device: master on, that trigger's toggle on, and the device is NOT actively viewing that
/// exact session on that device (other devices still get paged). The body is the redaction-safe
/// <see cref="NotificationPayload.ShortHint"/>, computed once here from the normalized event — never raw tool
/// arguments or file contents, since a push is commonly shown on a lock screen.
/// </para>
/// </summary>
public sealed class PushNotificationDispatcher : IEventObserver<BeforeAgentEventEvent>, IDisposable
{
    private readonly IPluginRegistry<INotificationChannel> _channels;
    private readonly PushRegistrationStore _registrations;
    private readonly ActiveSessionViewTracker _views;
    private readonly ILogger<PushNotificationDispatcher>? _logger;
    private readonly IDisposable _subscription;

    public PushNotificationDispatcher(
        IEventBus bus,
        IPluginRegistry<INotificationChannel> channels,
        PushRegistrationStore registrations,
        ActiveSessionViewTracker views,
        ILogger<PushNotificationDispatcher>? logger = null)
    {
        _channels = channels;
        _registrations = registrations;
        _views = views;
        _logger = logger;
        _subscription = bus.Observe(this);
    }

    public async ValueTask ObserveAsync(BeforeAgentEventEvent evt, CancellationToken cancellationToken = default)
    {
        if (!TryDescribe(evt.Event, out var trigger, out var shortHint))
        {
            return; // not a "worth paging a human" moment.
        }

        foreach (var registration in _registrations.All)
        {
            if (!registration.Enabled || !registration.Triggers.IsEnabled(trigger))
            {
                continue; // master off, or this trigger toggled off for the device.
            }

            if (_views.IsViewing(registration.DeviceId, evt.SessionId))
            {
                // Suppressed on THIS device only — the person looking at it here doesn't need a page here.
                continue;
            }

            var channel = _channels.Find(registration.ChannelId);
            if (channel is null)
            {
                _logger?.LogWarning(
                    "Device {Device} registered for unknown channel {Channel}; skipping push", registration.DeviceId, registration.ChannelId);
                continue;
            }

            var payload = new NotificationPayload(registration.DeviceId, trigger, shortHint, evt.SessionId);
            try
            {
                await channel.SendAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Independent delivery: one device/channel failing must not stop the others (or any other path).
                _logger?.LogWarning(ex, "Channel {Channel} failed to push to device {Device}", channel.Id, registration.DeviceId);
            }
        }
    }

    /// <summary>Maps a raw session event to its trigger + a minimized, redaction-safe hint, or false if the
    /// event is not a notification trigger. Kept in one place so redaction is audited once, not re-implemented
    /// per client head.</summary>
    private static bool TryDescribe(SessionEvent @event, out NotificationTrigger trigger, out string shortHint)
    {
        switch (@event)
        {
            case PermissionRequestedEvent permission:
                trigger = NotificationTrigger.PermissionRequest;
                // Title is the already-normalized tool-call summary — safe to show; not the raw arguments.
                shortHint = $"Permission: {permission.Title}";
                return true;
            case QuestionAskedEvent question:
                trigger = NotificationTrigger.UserActionRequest;
                var count = question.Questions.Count;
                shortHint = count == 1 ? "1 question to answer" : $"{count} questions to answer";
                return true;
            case TurnEndedEvent:
                trigger = NotificationTrigger.TurnReady;
                shortHint = "Turn finished — ready for you";
                return true;
            default:
                trigger = default;
                shortHint = string.Empty;
                return false;
        }
    }

    public void Dispose() => _subscription.Dispose();
}
