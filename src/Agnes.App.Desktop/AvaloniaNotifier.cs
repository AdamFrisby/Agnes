using Agnes.Ui.Core.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;

namespace Agnes.App.Desktop;

/// <summary>
/// Surfaces session notifications as in-app toasts via Avalonia's <see cref="WindowNotificationManager"/>
/// — cross-platform with no extra dependencies. Blockers and errors show as warnings/errors, completions
/// as information. This is the frontend's <see cref="INotifier"/>; a native OS-tray implementation could
/// be swapped in behind the same interface without touching the view models.
/// </summary>
public sealed class AvaloniaNotifier : INotifier
{
    private readonly WindowNotificationManager _manager;

    public AvaloniaNotifier(TopLevel topLevel)
    {
        _manager = new WindowNotificationManager(topLevel)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 4,
        };
    }

    public void Notify(AppNotification notification)
    {
        Dispatcher.UIThread.Post(() => _manager.Show(new Notification(
            notification.Title,
            notification.Body,
            Map(notification.Kind))));
    }

    private static NotificationType Map(NotificationKind kind) => kind switch
    {
        NotificationKind.Blocker => NotificationType.Warning,
        NotificationKind.Error => NotificationType.Error,
        _ => NotificationType.Information,
    };
}
