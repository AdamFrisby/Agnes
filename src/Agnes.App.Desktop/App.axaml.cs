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

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.RestoreAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
