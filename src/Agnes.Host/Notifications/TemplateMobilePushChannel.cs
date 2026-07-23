using System.Collections.Concurrent;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Notifications;

/// <summary>
/// The built-in <c>"mobile-push"</c> channel — a TEMPLATE STUB, not a real integration. It records every
/// device token it was handed and every payload it was asked to send (so tests and manual runs can see the
/// dispatch decisions), and otherwise does nothing over the network. Mobile OSes aggressively suspend/kill
/// backgrounded apps, so reaching a phone whose app isn't running requires a real OS push service — this class
/// is exactly where that wiring goes.
/// <para>
/// TO WIRE A REAL FCM / APNs CHANNEL:
/// <list type="number">
/// <item>Take this deployment's OWN FCM/APNs credentials by constructor injection (bring-your-own — no shared
/// project-run relay; see the idea doc's "Push credentials" section). Do NOT hardcode a shared key.</item>
/// <item>In <see cref="RegisterAsync"/>, persist the mapping from Agnes <c>deviceId</c> to the FCM registration
/// token / APNs device token (here it is just kept in memory).</item>
/// <item>In <see cref="SendAsync"/>, look up the token for <c>payload.DeviceId</c> and POST to FCM
/// (<c>https://fcm.googleapis.com/v1/projects/&lt;id&gt;/messages:send</c>) or connect to APNs — putting only
/// <c>payload.ShortHint</c> in the visible body (it is already minimized/redacted host-side) and
/// <c>payload.SessionId</c>/<c>Trigger</c> in the data section so a tap can route to the session and, for a
/// permission request, offer Allow/Deny that call back through <see cref="PushActionRouter"/> (never trusting
/// the payload itself).</item>
/// <item>Handle the service's own delivery/expiry/retry semantics rather than re-inventing them.</item>
/// </list>
/// </para>
/// </summary>
public sealed class TemplateMobilePushChannel : INotificationChannel
{
    private readonly ConcurrentDictionary<string, string> _tokensByDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<NotificationPayload> _sent = new();
    private readonly ILogger<TemplateMobilePushChannel>? _logger;

    public TemplateMobilePushChannel(ILogger<TemplateMobilePushChannel>? logger = null) => _logger = logger;

    public string Id => "mobile-push";

    /// <summary>Every payload this stub was asked to deliver, in order — inspection point for tests/diagnostics.</summary>
    public IReadOnlyList<NotificationPayload> SentPayloads => _sent.ToArray();

    /// <summary>The channel token last registered for a device, or null. (A real channel would persist this.)</summary>
    public string? TokenFor(string deviceId) => _tokensByDevice.GetValueOrDefault(deviceId);

    public Task RegisterAsync(string deviceId, string channelToken, CancellationToken ct = default)
    {
        // WIRE FCM/APNs HERE: persist deviceId -> registration token durably instead of in memory.
        _tokensByDevice[deviceId] = channelToken;
        _logger?.LogDebug("Mobile-push (template) registered token for device {Device}", deviceId);
        return Task.CompletedTask;
    }

    public Task SendAsync(NotificationPayload payload, CancellationToken ct = default)
    {
        // WIRE FCM/APNs HERE: POST to the push service using the stored token for payload.DeviceId.
        _sent.Enqueue(payload);
        _logger?.LogInformation(
            "Mobile-push (template, no-op) would push to device {Device} for session {Session} ({Trigger}): {Hint}",
            payload.DeviceId, payload.SessionId, payload.Trigger, payload.ShortHint);
        return Task.CompletedTask;
    }
}
