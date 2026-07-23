using System.Collections.Concurrent;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Notifications;

/// <summary>
/// The real <c>"fcm"</c> notification channel: it maps a minimized <see cref="NotificationPayload"/> to an FCM
/// message and hands it to <see cref="IFcmSender"/> (the seam over Google's Firebase Admin SDK). It supersedes
/// the <see cref="TemplateMobilePushChannel"/> stub when a service-account credential is configured
/// (bring-your-own, from host settings — see <c>.ideas/notifications/01-push-notifications.md</c>). The
/// Android/iOS FCM SDK integration is the client half and is out of scope here.
/// <para>
/// Config-gated: constructed with a non-null <see cref="IFcmSender"/> only when
/// <c>Agnes:Push:Fcm:ServiceAccountJson</c> (or a file path) is set. With no sender it is
/// <see cref="IsUsable"/> = false and <see cref="SendAsync"/> is a safe no-op, so the channel can be present
/// (and registered against) in dev/tests without a real credential.
/// </para>
/// <para>
/// The visible body is only <see cref="NotificationPayload.ShortHint"/> (already minimized/redacted host-side);
/// the session id and trigger ride the FCM <c>data</c> section so a tap can deep-link and, for a permission
/// request, route an Allow/Deny back through <see cref="PushActionRouter"/> — the payload itself is never
/// trusted to change an outcome. A send failure is logged and swallowed so one device can't break the others.
/// </para>
/// </summary>
public sealed class FcmPushChannel : INotificationChannel
{
    /// <summary>The stable channel id a device's push registration targets when FCM is the configured transport.</summary>
    public const string ChannelId = "fcm";

    private readonly ConcurrentDictionary<string, string> _tokensByDevice = new(StringComparer.Ordinal);
    private readonly IFcmSender? _sender;
    private readonly ILogger<FcmPushChannel>? _logger;

    /// <summary>Constructs the channel. A null <paramref name="sender"/> means no credential is configured —
    /// the channel is registered but reports <see cref="IsUsable"/> = false and never sends.</summary>
    public FcmPushChannel(IFcmSender? sender, ILogger<FcmPushChannel>? logger = null)
    {
        _sender = sender;
        _logger = logger;
    }

    public string Id => ChannelId;

    /// <summary>True only when a real FCM sender (i.e. a configured service-account credential) was supplied.
    /// When false, <see cref="SendAsync"/> is a no-op that never throws.</summary>
    public bool IsUsable => _sender is not null;

    /// <summary>The FCM registration token last stored for a device, or null. (Populated by
    /// <see cref="RegisterAsync"/>; exposed for tests/diagnostics.)</summary>
    public string? TokenFor(string deviceId) => _tokensByDevice.GetValueOrDefault(deviceId);

    public Task RegisterAsync(string deviceId, string channelToken, CancellationToken ct = default)
    {
        _tokensByDevice[deviceId] = channelToken;
        _logger?.LogDebug("FCM registered token for device {Device}", deviceId);
        return Task.CompletedTask;
    }

    public async Task SendAsync(NotificationPayload payload, CancellationToken ct = default)
    {
        if (_sender is null)
        {
            return; // config-gated: no FCM service-account credential, so there is nothing to deliver.
        }

        if (!_tokensByDevice.TryGetValue(payload.DeviceId, out var token) || string.IsNullOrEmpty(token))
        {
            _logger?.LogDebug("No FCM registration token for device {Device}; skipping push", payload.DeviceId);
            return;
        }

        var title = TitleFor(payload.Trigger);
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = payload.SessionId,
            ["deviceId"] = payload.DeviceId,
            ["trigger"] = payload.Trigger.ToString(),
        };

        try
        {
            await _sender.SendAsync(token, title, payload.ShortHint, data, ct).ConfigureAwait(false);
            _logger?.LogInformation(
                "FCM push delivered to device {Device} for session {Session} ({Trigger})",
                payload.DeviceId, payload.SessionId, payload.Trigger);
        }
        catch (Exception ex)
        {
            // Independent delivery: an FCM failure for one device is logged and swallowed so it can neither
            // affect the spine event that triggered it nor block pushes to the other eligible devices.
            _logger?.LogWarning(ex, "FCM push to device {Device} failed", payload.DeviceId);
        }
    }

    /// <summary>A short, fixed visible title per trigger. Deliberately generic — the sensitive specifics live in
    /// the already-minimized <see cref="NotificationPayload.ShortHint"/> body, not here.</summary>
    private static string TitleFor(NotificationTrigger trigger) => trigger switch
    {
        NotificationTrigger.TurnReady => "Agent ready",
        NotificationTrigger.PermissionRequest => "Permission needed",
        NotificationTrigger.UserActionRequest => "Action needed",
        _ => "Agnes",
    };
}
