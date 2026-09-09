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
        Dispatcher.UIThread.Post(() =>
        {
            if (notification.Kind == NotificationKind.File)
            {
                // A file arriving is the one kind whose card carries a glyph: the type badge is what says
                // "something to look at" at a glance, where the other kinds are told apart by their hue.
                _manager.Show(
                    FileContent(notification),
                    NotificationType.Information,
                    onClick: () => _onActivated?.Invoke(notification));
                return;
            }

            _manager.Show(new Notification(
                notification.Title,
                notification.Body,
                Map(notification.Kind),
                onClick: () => _onActivated?.Invoke(notification)));
        });
    }

    /// <summary>The toast body for a received file: the file glyph, then the same title/body text.</summary>
    private static Control FileContent(AppNotification notification)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = notification.Title,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = notification.Body,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.8,
        });

        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        // FontSize does not inherit through FluentIcons — it re-registers the property — so state it here.
        row.Children.Add(new FluentIcons.Avalonia.SymbolIcon
        {
            Symbol = FluentIcons.Common.Symbol.DocumentArrowDown,
            FontSize = 20,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        });
        row.Children.Add(text);
        return row;
    }

    private static NotificationType Map(NotificationKind kind) => kind switch
    {
        NotificationKind.Blocker => NotificationType.Warning,
        NotificationKind.Error => NotificationType.Error,
        _ => NotificationType.Information,
    };
}
