using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.Keymaps;
using Agnes.App.Desktop.ViewModels;
using Agnes.App.Desktop.Views;
using Agnes.Client;
using Agnes.Client.Simulation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop;

public partial class App : Application
{
    // Held for the app's lifetime so the tray icon + its menu aren't garbage-collected. Null when the
    // platform has no usable system tray (the app still runs, just without a tray presence).
    private TrayPresence? _tray;
    private AboutAgnesWindow? _aboutWindow;
    private KeymapService? _keymap;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        NativeMenu.SetMenu(this, DesktopBranding.CreateApplicationMenu(OnAboutAgnes));
    }

    private void OnAboutAgnes(object? sender, EventArgs e)
    {
        if (_aboutWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var about = new AboutAgnesWindow();
        _aboutWindow = about;
        about.Closed += (_, _) =>
        {
            if (ReferenceEquals(_aboutWindow, about))
            {
                _aboutWindow = null;
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
        {
            _ = about.ShowDialog(owner);
        }
        else
        {
            about.Show();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Routing connector: sim:// simulated, rec:// recorded playback, http(s):// SignalR.
            var recordingsDir = Environment.GetEnvironmentVariable("AGNES_RECORDINGS")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agnes", "recordings");
            IAgnesConnector connector = new RoutingConnector(recordingsDir);
            var settingsStore = new SettingsStore();
            _keymap = KeymapService.CreateDefault(settingsStore.FilePath);
            var viewModel = new MainWindowViewModel(
                connector, new AvaloniaDispatcher(), new SessionStateStore(), new HostRegistryStore(),
                settingsStore: settingsStore, keymap: _keymap);

            MainWindowViewModel.ApplyTheme(viewModel.Theme); // System / Light / Dark from settings

            var window = new MainWindow { DataContext = viewModel };
            window.InstallKeymap(_keymap);
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
            desktop.Exit += (_, _) => _keymap?.Dispose();

            // Additive system-tray presence: aggregate session status + jump-to-session, and close-to-tray.
            // Fully guarded — a desktop environment without tray support just gets no tray, no crash.
            _tray = TrayPresence.TryInstall(this, desktop, window, viewModel);

            WireLinkActivation(viewModel, window);

            _ = viewModel.RestoreAsync();
            _ = viewModel.CheckForUpdatesAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Connects every way an <c>agnes://</c> link can reach a running app to the one place that acts on it.
    ///
    /// There are three, because the platforms disagree about how a click is delivered. Linux and Windows start
    /// the registered executable with the URL as an argument — which is this process on a cold start, and a
    /// throwaway second process that forwards it (see <see cref="SingleInstance"/>) when Agnes is already up.
    /// macOS delivers neither: Launch Services sends the bundle an Apple Event, which Avalonia surfaces as a
    /// <see cref="ProtocolActivatedEventArgs"/> — so on that platform the argv paths never fire and this one
    /// does all the work.
    /// </summary>
    private void WireLinkActivation(MainWindowViewModel viewModel, MainWindow window)
    {
        void Handle(string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Any activation brings the window forward: being handed a link and left behind another
                // window would look like nothing happened.
                window.Show();
                if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                {
                    window.WindowState = Avalonia.Controls.WindowState.Normal;
                }

                window.Activate();

                if (UriScheme.IsSchemeArgument(message))
                {
                    viewModel.HandleLink(message);
                }
            });
        }

        // Started by a click while nothing was running.
        if (Program.LaunchLink is { Length: > 0 } launched)
        {
            Handle(launched);
        }

        // A later click, forwarded by the second launch that the instance gate turned away.
        if (Program.Instance is { } instance)
        {
            instance.MessageReceived += Handle;
        }

        // macOS, and any platform where Avalonia gets there first.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } protocolActivated)
                {
                    Handle(protocolActivated.Uri.ToString());
                }
            };
        }
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
