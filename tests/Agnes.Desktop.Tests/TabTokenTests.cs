using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>
/// Saved tabs and archived sessions carry a copy of their host's token from the day they were saved. The
/// host registry is where a re-pair puts the new one, so a tab that trusted its own copy came back after a
/// re-pair presenting a revoked token and failing 401 at every start, while the registry held a live one.
/// </summary>
public class TabTokenTests
{
    private const string Url = "https://lab.example:5081";

    private static (string Tabs, string Hosts, string Archive) TempPaths()
        => (Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-hosts-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-arch-{Guid.NewGuid():n}.json"));

    private static MainWindowViewModel NewVm(string tabs, string hosts, string archive)
        => new(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(tabs), new HostRegistryStore(hosts),
            new NullPromptStore(), new SessionStateStore(archive));

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => ((IDocumentDock)vm.Layout.VisibleDockables![0]).VisibleDockables!.OfType<SessionDocument>();

    private static SessionDescriptor Saved(string sessionId, string title)
        => new("Lab", Url, "stale-token", sessionId, "opencode", title);

    [Fact]
    public async Task A_restored_tab_takes_its_token_from_the_host_registry()
    {
        var (t, h, a) = TempPaths();
        new HostRegistryStore(h).Save([new KnownHost("Lab", Url, "fresh-token")]);
        new SessionStateStore(t).Save([Saved("s1", "dawn2")]);
        new SessionStateStore(a).Save([Saved("s2", "older work")]);

        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();

        Assert.Equal("fresh-token", Assert.Single(Tabs(vm)).Descriptor!.Token);
        Assert.Equal("fresh-token", Assert.Single(vm.ArchivedSessions).Token);
    }

    [Fact]
    public async Task A_tab_on_a_host_the_registry_forgot_keeps_its_own_token()
    {
        var (t, h, a) = TempPaths();
        new SessionStateStore(t).Save([Saved("s1", "dawn2")]);

        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();

        Assert.Equal("stale-token", Assert.Single(Tabs(vm)).Descriptor!.Token);
    }
}
