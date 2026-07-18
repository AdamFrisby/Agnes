using System.Diagnostics;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.App.Services;

/// <summary>
/// Surfaces session notifications on Uno as an in-app banner (via <see cref="Received"/>, shown by
/// the shell) and traces them. A native OS / push implementation can extend this behind the same
/// <see cref="INotifier"/> interface.
/// </summary>
public sealed class UnoNotifier : INotifier
{
    /// <summary>Raised for each notification; the shell shows it as an InfoBar.</summary>
    public event Action<AppNotification>? Received;

    public void Notify(AppNotification notification)
    {
        Debug.WriteLine($"[Agnes:{notification.Kind}] {notification.Title} — {notification.Body}");
        Received?.Invoke(notification);
    }
}
