using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Plugins;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>A client plugin's custom screen opens as a dock document (tab), the same way the built-in
/// Settings screen replaces the conversation view (see .ideas/00d-event-spine-and-ui-extensibility.md, AC7).</summary>
public class CustomScreenTests
{
    private sealed record FakeScreen(string ScreenId, string Title, string? Icon, object Vm) : ICustomScreenProvider
    {
        public object CreateViewModel() => Vm;
    }

    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static MainWindowViewModel NewVm()
        => new(new SimulatedConnector(), ImmediateDispatcher.Instance, new SessionStateStore(), new HostRegistryStore());

    [Fact]
    public void Opening_a_custom_screen_adds_a_plugin_document_to_the_dock()
    {
        var vm = NewVm();
        var screenVm = new object();
        var provider = new FakeScreen("myplugin.dashboard", "Dashboard", "📊", screenVm);

        vm.OpenCustomScreen(provider);

        var doc = Assert.Single(DocumentDock(vm).VisibleDockables!.OfType<PluginScreenDocument>());
        Assert.Equal("myplugin.dashboard", doc.Id);
        Assert.Equal("Dashboard", doc.Title);
        Assert.Same(screenVm, doc.ScreenViewModel);
    }

    [Fact]
    public void Opening_the_same_custom_screen_twice_reuses_the_one_document()
    {
        var vm = NewVm();
        var provider = new FakeScreen("myplugin.dashboard", "Dashboard", null, new object());

        vm.OpenCustomScreen(provider);
        vm.OpenCustomScreen(provider);

        Assert.Single(DocumentDock(vm).VisibleDockables!.OfType<PluginScreenDocument>());
    }
}
