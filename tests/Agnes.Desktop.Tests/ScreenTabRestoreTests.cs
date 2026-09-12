using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Plugins;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>
/// A plugin screen tab survives a restart the way session tabs do. The CodeyBox tab an operator keeps open
/// vanished at every restart while the sessions beside it came back.
/// </summary>
public class ScreenTabRestoreTests
{
    private sealed record FakeScreen(string ScreenId, string Title, string? Icon) : ICustomScreenProvider
    {
        public object CreateViewModel() => new object();
    }

    private sealed class FakeModule(ICustomScreenProvider screen) : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector) => collector.AddCustomScreen(screen);
    }

    private static (string Tabs, string Hosts) TempPaths()
        => (Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-hosts-{Guid.NewGuid():n}.json"));

    private static MainWindowViewModel NewVm(string tabs, string hosts, IClientPluginModule? module)
        => new(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(tabs), new HostRegistryStore(hosts),
            new NullPromptStore(),
            clientPluginDirectory: Path.Combine(Path.GetTempPath(), $"agnes-no-plugins-{Guid.NewGuid():n}"),
            clientPluginModules: module is null ? null : [module]);

    private static IDocumentDock Dock(MainWindowViewModel vm) => (IDocumentDock)vm.Layout.VisibleDockables![0];

    [Fact]
    public async Task An_open_plugin_screen_is_saved_and_comes_back_after_a_restart()
    {
        var (t, h) = TempPaths();
        var screen = new FakeScreen("codeybox.queue", "CodeyBox", null);

        var first = NewVm(t, h, new FakeModule(screen));
        await first.RestoreAsync();
        first.OpenCustomScreen(screen);
        first.PersistTabs();

        var saved = new SessionStateStore(t).Load();
        var record = Assert.Single(saved, d => d.IsScreen);
        Assert.Equal("codeybox.queue", record.ScreenId);
        Assert.Equal("CodeyBox", record.Title);

        var second = NewVm(t, h, new FakeModule(screen));
        await second.RestoreAsync();
        var doc = Assert.Single(Dock(second).VisibleDockables!.OfType<PluginScreenDocument>());
        Assert.Equal("codeybox.queue", doc.Id);
    }

    [Fact]
    public async Task A_saved_screen_whose_plugin_is_gone_is_dropped_quietly()
    {
        var (t, h) = TempPaths();
        new SessionStateStore(t).Save([SessionDescriptor.ForScreen("vanished.plugin", "Gone")]);

        var vm = NewVm(t, h, module: null);
        await vm.RestoreAsync();

        Assert.Empty(Dock(vm).VisibleDockables!.OfType<PluginScreenDocument>());
    }

    [Fact]
    public void Old_tab_files_without_a_screen_field_still_read_as_sessions()
    {
        var descriptor = new SessionDescriptor("Lab", "https://lab:5081", "t", "s1", "opencode", "dawn2");
        Assert.False(descriptor.IsScreen);
    }
}
