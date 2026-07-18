using System.Diagnostics;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.App.Services;

/// <summary>
/// Surfaces session notifications on Uno. A minimal implementation for now (traces the
/// notification); a native OS / push implementation drops in behind the same <see cref="INotifier"/>.
/// </summary>
public sealed class UnoNotifier : INotifier
{
    public void Notify(AppNotification notification)
        => Debug.WriteLine($"[Agnes:{notification.Kind}] {notification.Title} — {notification.Body}");
}
