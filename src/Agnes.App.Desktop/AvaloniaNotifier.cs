using Agnes.Ui.Core.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;

namespace Agnes.App.Desktop;

/// <summary>
/// The desktop <see cref="INotifier"/>. When the window is focused, shows an in-app toast (via
/// Avalonia's <see cref="WindowNotificationManager"/>); when it's in the background, fires a real
/// OS notification (<see cref="NativeOsNotifier"/>) so a finished or blocked turn reaches the user
/// even when Agnes isn't the active window.
/// </summary>
public sealed class AvaloniaNotifier : INotifier
{
    private readonly WindowNotificationManager _manager;
    private readonly Func<bool> _isWindowActive;
    private readonly Action<AppNotification>? _onActivated;

    public AvaloniaNotifier(TopLevel topLevel, Func<bool>? isWindowActive = null, Action<AppNotification>? onActivated = null)
    {
        _manager = new WindowNotificationManager(topLevel)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 4,
        };
        _isWindowActive = isWindowActive ?? (() => true);
        _onActivated = onActivated;
    }

    public void Notify(AppNotification notification)
    {
        if (!_isWindowActive())
        {
            NativeOsNotifier.Notify(notification.Title, notification.Body);
            return;
        }

        // Clicking the toast jumps to the session (and the specific item) that raised it.
        Dispatcher.UIThread.Post(() => _manager.Show(new Notification(
            notification.Title,
            notification.Body,
            Map(notification.Kind),
            onClick: () => _onActivated?.Invoke(notification))));
    }

    private static NotificationType Map(NotificationKind kind) => kind switch
    {
        NotificationKind.Blocker => NotificationType.Warning,
        NotificationKind.Error => NotificationType.Error,
        _ => NotificationType.Information,
    };
}
