using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

public sealed class TabClosingTests
{
    [Fact]
    public async Task Close_active_tab_command_closes_settings_and_dashboard_tabs()
    {
        var id = Guid.NewGuid().ToString("n");
        var vm = new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
        await vm.RestoreAsync();
        var dock = Assert.IsAssignableFrom<IDocumentDock>(vm.Layout.VisibleDockables![0]);

        vm.OpenSettingsCommand.Execute(null);
        var settings = Assert.Single(dock.VisibleDockables!.OfType<SettingsDocument>());
        Assert.Same(settings, dock.ActiveDockable);

        await vm.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.DoesNotContain(settings, dock.VisibleDockables!);

        vm.OpenDashboardCommand.Execute(null);
        var dashboard = Assert.Single(dock.VisibleDockables!.OfType<DashboardDocument>());
        Assert.Same(dashboard, dock.ActiveDockable);

        await vm.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.DoesNotContain(dashboard, dock.VisibleDockables!);
        Assert.Null(vm.Dashboard);
    }
}
