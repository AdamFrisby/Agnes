using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>
/// Rejoining work that's already running: the tab lists the host's sessions the moment it connects, joining
/// one attaches it in place (nothing new is opened), and the dashboard tab gathers the same facts across
/// every connected host with the attention band on top.
/// </summary>
public class SessionCatalogueTabTests
{
    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => DocumentDock(vm).VisibleDockables!.OfType<SessionDocument>();

    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
    }

    private static async Task<SessionDocument> ConnectAsync(MainWindowViewModel vm)
    {
        var tab = Tabs(vm).Last();
        await WaitAsync(() => tab.Hosts is { Count: > 0 });
        tab.Hosts!.First().Select.Execute(null);
        await WaitAsync(() => tab.Agents is { Count: > 0 });
        return tab;
    }

    [Fact]
    public async Task Connecting_to_a_host_lists_what_it_is_already_running()
    {
        var vm = NewVm();
        await vm.RestoreAsync();

        var tab = await ConnectAsync(vm);
        await WaitAsync(() => tab.HostSessions.HasSessions);

        // The picker and the catalogue are offered together — start something new, or pick up what's there.
        Assert.Equal(TabStage.PickAgent, tab.Stage);
        Assert.All(tab.HostSessions.Sessions, r => Assert.False(string.IsNullOrEmpty(r.Title)));
    }

    [Fact]
    public async Task Joining_a_listed_session_attaches_it_in_this_tab()
    {
        var vm = NewVm();
        await vm.RestoreAsync();

        var tab = await ConnectAsync(vm);
        await WaitAsync(() => tab.HostSessions.HasSessions);
        var row = tab.HostSessions.Sessions[0];

        tab.HostSessions.AttachCommand.Execute(row);
        await WaitAsync(() => tab.Session is not null);

        Assert.Equal(row.SessionId, tab.Session!.SessionId);
        Assert.Equal(TabStage.Live, tab.Stage);
        // Joining is not opening: the tab holds the session that was already there, under its own name.
        Assert.Equal(row.SessionId, tab.Descriptor!.SessionId);
        Assert.Single(Tabs(vm));
    }

    [Fact]
    public async Task Joining_a_session_this_window_already_holds_focuses_it_instead_of_duplicating_it()
    {
        var vm = NewVm();
        await vm.RestoreAsync();

        var tab = await ConnectAsync(vm);
        await WaitAsync(() => tab.HostSessions.HasSessions);
        var row = tab.HostSessions.Sessions[0];
        tab.HostSessions.AttachCommand.Execute(row);
        await WaitAsync(() => tab.Session is not null);

        // A second tab on the same host offers the same session; taking it lands back on the first tab.
        vm.NewTabCommand.Execute(null);
        var second = await ConnectAsync(vm);
        await WaitAsync(() => second.HostSessions.HasSessions);
        var same = second.HostSessions.Sessions.FirstOrDefault(r => r.SessionId == row.SessionId);
        Assert.NotNull(same);
        Assert.True(same!.IsAlreadyOpen);

        second.HostSessions.AttachCommand.Execute(same);
        await Task.Delay(100);
        Assert.Null(second.Session); // nothing was subscribed twice
    }

    [Fact]
    public async Task Dashboard_opens_as_a_tab_and_shows_the_live_session_and_the_ones_running_elsewhere()
    {
        var vm = NewVm();
        await vm.RestoreAsync();

        var tab = await ConnectAsync(vm);
        await WaitAsync(() => tab.HostSessions.HasSessions);
        var joined = tab.HostSessions.Sessions[0];
        tab.HostSessions.AttachCommand.Execute(joined);
        await WaitAsync(() => tab.Session is not null);

        vm.OpenDashboardCommand.Execute(null);

        var dashboard = Assert.Single(DocumentDock(vm).VisibleDockables!.OfType<DashboardDocument>());
        await WaitAsync(() => dashboard.Dashboard.HasLive);

        // The joined session is a live card; the rest of the host's catalogue is offered to join.
        Assert.Contains(dashboard.Dashboard.Live, r => r.SessionId == joined.SessionId);
        await WaitAsync(() => dashboard.Dashboard.HasElsewhere);
        Assert.DoesNotContain(dashboard.Dashboard.Elsewhere, r => r.SessionId == joined.SessionId);

        // Opening it again focuses the one tab rather than stacking a second.
        vm.OpenDashboardCommand.Execute(null);
        Assert.Single(DocumentDock(vm).VisibleDockables!.OfType<DashboardDocument>());
    }

    [Fact]
    public async Task Closing_the_dashboard_disposes_it_so_it_stops_polling()
    {
        var vm = NewVm();
        await vm.RestoreAsync();
        vm.OpenDashboardCommand.Execute(null);

        var dashboard = Assert.Single(DocumentDock(vm).VisibleDockables!.OfType<DashboardDocument>());
        Assert.NotNull(vm.Dashboard);

        vm.Factory.CloseDockable(dashboard);

        Assert.Empty(DocumentDock(vm).VisibleDockables!.OfType<DashboardDocument>());
        Assert.Null(vm.Dashboard);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }
}
