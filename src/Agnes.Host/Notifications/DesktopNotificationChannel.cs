using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Notifications;

/// <summary>
/// The host-side seam a co-located desktop client implements to hand a push off to the existing client-side
/// notifier path (<c>NativeOsNotifier</c>/<c>AvaloniaNotifier</c> over <c>INotifier</c>/<c>AppNotification</c>
/// in <c>Agnes.Ui.Core</c>, which <c>Agnes.Host</c> deliberately does not reference). Desktop is a long-running
/// process, so a normal OS toast fired from it is enough — the <see cref="DesktopNotificationChannel"/> exists
/// only so that path is reachable as a uniform <see cref="INotificationChannel"/>. The default host binding is
/// <see cref="LoggingDesktopNotificationSink"/> (a no-op log); an embedded-desktop deployment swaps in an
/// implementation that maps the payload to an <c>AppNotification</c> and calls its real notifier.
/// </summary>
public interface IDesktopNotificationSink
{
    Task NotifyAsync(NotificationPayload payload, CancellationToken ct = default);
}

/// <summary>Default desktop sink: logs the (already-minimized) payload. Non-regression by construction — it
/// touches nothing about the existing desktop notification path, which keeps firing from the client exactly as
/// before; this only adds the host-side channel surface over it.</summary>
public sealed class LoggingDesktopNotificationSink : IDesktopNotificationSink
{
    private readonly ILogger<LoggingDesktopNotificationSink>? _logger;

    public LoggingDesktopNotificationSink(ILogger<LoggingDesktopNotificationSink>? logger = null) => _logger = logger;

    public Task NotifyAsync(NotificationPayload payload, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "Desktop notification for device {Device} on session {Session} ({Trigger}): {Hint}",
            payload.DeviceId, payload.SessionId, payload.Trigger, payload.ShortHint);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The built-in <c>"desktop"</c> notification channel: wraps/forwards to the existing desktop notifier path via
/// <see cref="IDesktopNotificationSink"/>. Registration is a no-op — a desktop process needs no push token, it
/// just shows a local OS toast — so <see cref="RegisterAsync"/> records nothing.
/// </summary>
public sealed class DesktopNotificationChannel : INotificationChannel
{
    private readonly IDesktopNotificationSink _sink;

    public DesktopNotificationChannel(IDesktopNotificationSink sink) => _sink = sink;

    public string Id => "desktop";

    public Task RegisterAsync(string deviceId, string channelToken, CancellationToken ct = default)
        => Task.CompletedTask; // no push token needed: the desktop process shows a local OS notification.

    public Task SendAsync(NotificationPayload payload, CancellationToken ct = default)
        => _sink.NotifyAsync(payload, ct);
}
