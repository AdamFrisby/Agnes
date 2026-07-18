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
            // Routing connector: sim:// hosts use the simulated server, http(s):// use SignalR.
            IAgnesConnector connector = new RoutingConnector();
            var viewModel = new MainWindowViewModel(
                connector, new AvaloniaDispatcher(), new SessionStateStore(), new HostRegistryStore());

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.RestoreAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
