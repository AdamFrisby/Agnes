using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>
/// How the top-bar search orders what it finds. It searches every open session, but the tab being read is
/// the likeliest target — searching mid-session usually means "find it in <i>this</i> one" — so those hits
/// are a group of their own ahead of the rest, rather than being interleaved by dock position.
/// </summary>
public class GlobalSearchGroupingTests
{
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => DocumentDock(vm).VisibleDockables!.OfType<SessionDocument>();

    /// <summary>Drives a fresh tab through the host → agent flow to a live session.</summary>
    private static async Task<SessionDocument> OpenSessionAsync(MainWindowViewModel vm)
    {
        var tab = Tabs(vm).Last();
        await WaitAsync(() => tab.Hosts is { Count: > 0 });
        tab.Hosts!.First().Select.Execute(null);
        await WaitAsync(() => tab.Agents is { Count: > 0 });
        tab.WorkingDirectory = "/tmp/agnes";
        tab.SelectAgentChoiceCommand.Execute(tab.Agents!.First(a => a.AdapterId == "opencode"));
        tab.StartSessionCommand.Execute(null);
        await WaitAsync(() => tab.Session is not null);
        await WaitAsync(() => tab.Session!.Items.Count > 0);
        return tab;
    }

    /// <summary>A window with two live sessions in two tabs, so "this one" and "the others" both exist.</summary>
    private static async Task<(MainWindowViewModel Vm, SessionDocument First, SessionDocument Second)> TwoLiveTabsAsync()
    {
        var id = Guid.NewGuid().ToString("n");
        var vm = new MainWindowViewModel(
            new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-search-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-search-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-search-arch-{id}.json")));

        await vm.RestoreAsync();
        var first = await OpenSessionAsync(vm);
        vm.NewTabCommand.Execute(null);
        var second = await OpenSessionAsync(vm);
        return (vm, first, second);
    }

    /// <summary>A term the scripted transcript is certain to contain — every simulated session opens with a
    /// "Session ready on …" greeting — so the test measures grouping and not the simulator's choice of words.</summary>
    private const string Term = "ready";

    [Fact]
    public async Task Hits_in_the_focused_tab_are_grouped_ahead_of_every_other_session()
    {
        var (vm, first, second) = await TwoLiveTabsAsync();
        vm.Factory.SetActiveDockable(first);

        vm.GlobalSearchQuery = Term;

        // Preconditions, stated so a failure reads as "the simulator changed" rather than "grouping broke".
        Assert.True(vm.HasThisSessionResults, "the focused tab should have matched");
        Assert.True(vm.HasOtherSessionResults, "the other tab should have matched");

        // The split is by tab, not by rank: every hit in a group belongs to that group's session.
        Assert.All(vm.ThisSessionResults, h => Assert.Same(first, h.Tab));
        Assert.All(vm.OtherSessionResults, h => Assert.Same(second, h.Tab));

        Assert.True(vm.HasBothResultGroups);
        Assert.True(vm.HasGlobalResults);
    }

    [Fact]
    public async Task Only_the_other_group_names_its_session()
    {
        var (vm, first, _) = await TwoLiveTabsAsync();
        vm.Factory.SetActiveDockable(first);

        vm.GlobalSearchQuery = Term;

        // The "This session" heading already says whose these are; repeating one title down every row of
        // that group is noise. A hit from elsewhere is useless without knowing which tab it came from.
        Assert.All(vm.ThisSessionResults, h => Assert.False(h.ShowSessionTitle));
        Assert.All(vm.OtherSessionResults, h => Assert.True(h.ShowSessionTitle));
        Assert.All(vm.OtherSessionResults, h => Assert.False(string.IsNullOrWhiteSpace(h.SessionTitle)));
    }

    [Fact]
    public async Task Changing_the_focused_tab_regroups_the_same_query()
    {
        var (vm, first, second) = await TwoLiveTabsAsync();

        vm.Factory.SetActiveDockable(first);
        vm.GlobalSearchQuery = Term;
        Assert.NotEmpty(vm.ThisSessionResults);
        Assert.All(vm.ThisSessionResults, h => Assert.Same(first, h.Tab));

        // Re-running the query with the other tab in front moves the groups with it — "this session" is
        // resolved per search, not captured once.
        vm.Factory.SetActiveDockable(second);
        vm.GlobalSearchQuery = string.Empty;
        vm.GlobalSearchQuery = Term;

        Assert.All(vm.ThisSessionResults, h => Assert.Same(second, h.Tab));
        Assert.All(vm.OtherSessionResults, h => Assert.Same(first, h.Tab));
    }

    [Fact]
    public async Task An_empty_query_clears_both_groups()
    {
        var (vm, first, _) = await TwoLiveTabsAsync();
        vm.Factory.SetActiveDockable(first);

        vm.GlobalSearchQuery = Term;
        vm.GlobalSearchQuery = "   ";

        Assert.Empty(vm.ThisSessionResults);
        Assert.Empty(vm.OtherSessionResults);
        Assert.False(vm.HasGlobalResults);
        Assert.False(vm.HasBothResultGroups);
    }
}
