using Agnes.App.Desktop.ViewModels;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Agnes.App.Desktop;

/// <summary>
/// Cross-platform system-tray presence for the desktop app — a thin Avalonia shell over the
/// framework-agnostic <see cref="TrayStatusViewModel"/>. It renders the aggregate idle/working/needs-attention
/// status as a tray icon + tooltip and a right-click menu that jumps straight to a session needing attention,
/// without restoring the full window. Purely additive and fully guarded: a desktop environment without tray
/// support degrades to "feature absent", never a crash, and the app still starts.
///
/// Because closing the window now keeps the process (and therefore all host connections and in-flight turns)
/// alive in the tray, this switches the lifetime to explicit shutdown and routes window-close to hide-to-tray.
/// That path is only ever taken when the tray actually installed, so a platform without a tray keeps the
/// original "closing the window quits" behavior and can never be left with no way to quit.
/// </summary>
internal sealed class TrayPresence
{
    private static readonly Color AttentionColor = Color.FromRgb(0xD9, 0x77, 0x57);
    private static readonly Color IdleColor = Color.FromRgb(0x98, 0xA2, 0xB3);

    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayStatusViewModel _status;
    private readonly TrayIcon _icon;
    private readonly WindowIcon _idleIcon;
    private readonly WindowIcon _attentionIcon;
    private bool _quitting;

    private TrayPresence(
        Application app,
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window,
        MainWindowViewModel viewModel)
    {
        _app = app;
        _desktop = desktop;
        _window = window;
        _viewModel = viewModel;
        _status = new TrayStatusViewModel(viewModel.OpenSessions);
        _idleIcon = MakeIcon(IdleColor);
        _attentionIcon = MakeIcon(AttentionColor);
        _icon = new TrayIcon
        {
            Icon = _status.HasAttention ? _attentionIcon : _idleIcon,
            ToolTipText = _status.Tooltip,
            IsVisible = true,
        };
    }

    /// <summary>
    /// Attempts to install the tray icon. Wrapped end-to-end in a try/catch so a missing/unsupported system
    /// tray (common on minimal Linux desktops) simply yields no tray rather than a startup failure. Returns
    /// true when the tray was installed (and close-to-tray is now active).
    /// </summary>
    public static TrayPresence? TryInstall(
        Application app,
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window,
        MainWindowViewModel viewModel)
    {
        try
        {
            var presence = new TrayPresence(app, desktop, window, viewModel);
            presence.Install();
            return presence;
        }
        catch
        {
            // No usable system tray on this platform/DE, or icon creation failed: run without a tray.
            return null;
        }
    }

    private void Install()
    {
        RebuildMenu();

        _status.ActivateRequested += OnActivateRequested;
        _status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrayStatusViewModel.Tooltip))
            {
                _icon.ToolTipText = _status.Tooltip;
            }
            else if (e.PropertyName is nameof(TrayStatusViewModel.HasAttention))
            {
                _icon.Icon = _status.HasAttention ? _attentionIcon : _idleIcon;
            }
        };
        _status.NeedsAttention.CollectionChanged += (_, _) => RebuildMenu();
        _icon.Clicked += (_, _) => ShowWindow();

        // Attach the icon to the application (throws / no-ops on platforms without a tray, hence the guard).
        TrayIcon.SetIcons(_app, [_icon]);

        // Close-to-tray: keep the process — and every host connection / in-flight turn — alive when the window
        // is closed, exactly as minimizing already does. Only engaged once the tray is genuinely present.
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _window.Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        // Hide to tray instead of tearing the app down; the tray "Show Agnes" entry brings it back.
        e.Cancel = true;
        _window.Hide();
    }

    private void OnActivateRequested(string sessionId)
    {
        _viewModel.ActivateSessionById(sessionId);
        ShowWindow();
    }

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void Quit()
    {
        _quitting = true;
        _desktop.Shutdown();
    }

    // Rebuild the right-click menu to list the sessions currently needing attention, each jumping to its tab,
    // plus a show/quit pair. Cheap and only fires when the attention set changes.
    private void RebuildMenu()
    {
        var menu = new NativeMenu();
        if (_status.NeedsAttention.Count == 0)
        {
            menu.Add(new NativeMenuItem("Nothing needs you right now") { IsEnabled = false });
        }
        else
        {
            menu.Add(new NativeMenuItem("Needs attention") { IsEnabled = false });
            foreach (var row in _status.NeedsAttention)
            {
                var sessionId = row.SessionId;
                var item = new NativeMenuItem($"    {row.Title}");
                item.Click += (_, _) => OnActivateRequested(sessionId);
                menu.Add(item);
            }
        }

        menu.Add(new NativeMenuItemSeparator());

        var show = new NativeMenuItem("Show Agnes");
        show.Click += (_, _) => ShowWindow();
        menu.Add(show);

        var quit = new NativeMenuItem("Quit Agnes");
        quit.Click += (_, _) => Quit();
        menu.Add(quit);

        _icon.Menu = menu;
    }

    // A tiny solid dot rendered in software (no asset needed); recolored to signal the attention state.
    private static WindowIcon MakeIcon(Color color)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawEllipse(new SolidColorBrush(color), null, new Rect(2, 2, 28, 28));
        }

        return new WindowIcon(bitmap);
    }
}
