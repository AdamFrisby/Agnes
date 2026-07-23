using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
using Agnes.Client.Simulation;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop;

public partial class App : Application
{
    // Held for the app's lifetime so the tray icon + its menu aren't garbage-collected. Null when the
    // platform has no usable system tray (the app still runs, just without a tray presence).
    private TrayPresence? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Routing connector: sim:// simulated, rec:// recorded playback, http(s):// SignalR.
            var recordingsDir = Environment.GetEnvironmentVariable("AGNES_RECORDINGS")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agnes", "recordings");
            IAgnesConnector connector = new RoutingConnector(recordingsDir);
            var viewModel = new MainWindowViewModel(
                connector, new AvaloniaDispatcher(), new SessionStateStore(), new HostRegistryStore());

            MainWindowViewModel.ApplyTheme(viewModel.Theme); // System / Light / Dark from settings

            var window = new MainWindow { DataContext = viewModel };
            // In-app toast when focused; native OS notification when the window is in the background.
            // Clicking a toast brings the window forward and jumps to the session + item it came from.
            viewModel.Notifier = new AvaloniaNotifier(
                window,
                () => viewModel.WindowActive,
                onActivated: n =>
                {
                    // If the session lives in a detached window, ActivateNotification focuses it;
                    // otherwise bring the main window forward.
                    if (!viewModel.ActivateNotification(n))
                    {
                        window.Activate();
                    }
                });
            window.Activated += (_, _) => viewModel.WindowActive = true;
            window.Deactivated += (_, _) => viewModel.WindowActive = false;

            RestoreWindowGeometry(window, viewModel.Settings);
            window.Closing += (_, _) => SaveWindowGeometry(window, viewModel);

            desktop.MainWindow = window;

            // Additive system-tray presence: aggregate session status + jump-to-session, and close-to-tray.
            // Fully guarded — a desktop environment without tray support just gets no tray, no crash.
            _tray = TrayPresence.TryInstall(this, desktop, window, viewModel);

            _ = viewModel.RestoreAsync();
            _ = viewModel.CheckForUpdatesAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RestoreWindowGeometry(MainWindow window, Persistence.AppSettings settings)
    {
        window.Width = settings.WindowWidth;
        window.Height = settings.WindowHeight;
        if (settings.WindowX != int.MinValue && settings.WindowY != int.MinValue)
        {
            window.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual;
            window.Position = new PixelPoint(settings.WindowX, settings.WindowY);
        }

        if (settings.WindowMaximized)
        {
            window.WindowState = Avalonia.Controls.WindowState.Maximized;
        }
    }

    private static void SaveWindowGeometry(MainWindow window, MainWindowViewModel vm)
    {
        if (window.WindowState == Avalonia.Controls.WindowState.Maximized)
        {
            // Keep the last normal size/position; just record that it was maximized.
            var s = vm.Settings;
            vm.SaveWindowState(s.WindowWidth, s.WindowHeight, s.WindowX, s.WindowY, maximized: true);
        }
        else
        {
            vm.SaveWindowState(window.Width, window.Height, window.Position.X, window.Position.Y, maximized: false);
        }
    }
}
