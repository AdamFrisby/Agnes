using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

public class SessionManagementTests
{
    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => DocumentDock(vm).VisibleDockables!.OfType<SessionDocument>();

    private static (string Tabs, string Hosts, string Archive) TempPaths()
        => (Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-hosts-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-arch-{Guid.NewGuid():n}.json"));

    private static MainWindowViewModel NewVm(string tabs, string hosts, string archive)
        => new(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(tabs), new HostRegistryStore(hosts),
            new NullPromptStore(), new SessionStateStore(archive));

    private static async Task<SessionDocument> OpenSessionAsync(MainWindowViewModel vm)
    {
        var tab = Tabs(vm).Last();
        await WaitAsync(() => tab.Hosts is { Count: > 0 });
        tab.Hosts!.First().Select.Execute(null);
        await WaitAsync(() => tab.Agents is { Count: > 0 });
        tab.Agents!.First(a => a.AdapterId == "opencode").Open.Execute(null);
        await WaitAsync(() => tab.Session is not null);
        return tab;
    }

    [Fact]
    public async Task Rename_updates_the_title_and_persists()
    {
        var (t, h, a) = TempPaths();
        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();
        var tab = await OpenSessionAsync(vm);

        tab.BeginRenameCommand.Execute(null);
        tab.RenameText = "Refactor auth";
        tab.CommitRenameCommand.Execute(null);

        Assert.Equal("Refactor auth", tab.Title);
        Assert.False(tab.IsRenaming);
        Assert.Equal("Refactor auth", new SessionStateStore(t).Load().Single().Title);
    }

    [Fact]
    public async Task Pin_and_tag_persist_and_restore()
    {
        var (t, h, a) = TempPaths();
        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();
        var tab = await OpenSessionAsync(vm);

        tab.TogglePinCommand.Execute(null);
        tab.TagInput = "backend";
        tab.AddTagCommand.Execute(null);
        tab.TagInput = "backend"; // duplicate ignored
        tab.AddTagCommand.Execute(null);

        Assert.True(tab.Pinned);
        Assert.Equal(["backend"], tab.Tags);

        var saved = new SessionStateStore(t).Load().Single();
        Assert.True(saved.Pinned);
        Assert.Equal(["backend"], saved.Tags!);

        // Relaunch restores pin + tags.
        var vm2 = NewVm(t, h, a);
        await vm2.RestoreAsync();
        var restored = Tabs(vm2).Single();
        Assert.True(restored.Pinned);
        Assert.Contains("backend", restored.Tags);

        tab.RemoveTagCommand.Execute("backend");
        Assert.Empty(tab.Tags);
    }

    [Fact]
    public async Task Archive_removes_the_tab_and_reopen_restores_it()
    {
        var (t, h, a) = TempPaths();
        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();
        var tab = await OpenSessionAsync(vm);
        tab.BeginRenameCommand.Execute(null);
        tab.RenameText = "Archived work";
        tab.CommitRenameCommand.Execute(null);

        tab.ArchiveCommand.Execute(null);

        Assert.Empty(Tabs(vm));
        var archived = Assert.Single(vm.ArchivedSessions);
        Assert.Equal("Archived work", archived.Title);
        Assert.True(vm.HasArchived);
        Assert.Equal("Archived work", new SessionStateStore(a).Load().Single().Title);

        vm.ReopenArchivedCommand.Execute(archived);

        var reopened = Assert.Single(Tabs(vm));
        Assert.Equal("Archived work", reopened.Title);
        Assert.Empty(vm.ArchivedSessions);
        await WaitAsync(() => reopened.Session is not null);
    }

    [Fact]
    public async Task Duplicate_opens_a_second_view_of_the_same_session()
    {
        var (t, h, a) = TempPaths();
        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();
        var tab = await OpenSessionAsync(vm);
        var sessionId = tab.Descriptor!.SessionId;

        await ((IAsyncRelayCommand)tab.DuplicateCommand).ExecuteAsync(null);

        Assert.Equal(2, Tabs(vm).Count());
        var copy = Tabs(vm).Last();
        await WaitAsync(() => copy.Session is not null);
        Assert.Equal(sessionId, copy.Descriptor!.SessionId); // same session
        Assert.Contains("view", copy.Title!);
    }

    [Fact]
    public async Task Fork_opens_a_new_independent_session()
    {
        var (t, h, a) = TempPaths();
        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();
        var tab = await OpenSessionAsync(vm);
        var sessionId = tab.Descriptor!.SessionId;

        await ((IAsyncRelayCommand)tab.ForkCommand).ExecuteAsync(null);

        Assert.Equal(2, Tabs(vm).Count());
        var fork = Tabs(vm).Last();
        await WaitAsync(() => fork.Session is not null);
        Assert.NotEqual(sessionId, fork.Descriptor!.SessionId); // new session
        Assert.Contains("fork", fork.Title!);
    }

    [Fact]
    public async Task Global_search_finds_hits_across_open_sessions_and_jumps_to_them()
    {
        var (t, h, a) = TempPaths();
        var vm = NewVm(t, h, a);
        await vm.RestoreAsync();

        var first = await OpenSessionAsync(vm);
        await WaitAsync(() => first.Session!.Items.Count > 0);
        vm.NewTabCommand.Execute(null);
        var second = await OpenSessionAsync(vm);
        await WaitAsync(() => second.Session!.Items.Count > 0);

        // Both sessions open with a "Session ready on OpenCode" greeting.
        vm.GlobalSearchQuery = "ready";

        Assert.True(vm.HasGlobalResults);
        Assert.True(vm.GlobalResults.Count >= 2);
        Assert.Contains(vm.GlobalResults, r => ReferenceEquals(r.Tab, first));
        Assert.Contains(vm.GlobalResults, r => ReferenceEquals(r.Tab, second));

        // Jumping to a hit scrolls its session (raised as a deep-link request).
        string? scrolled = null;
        first.Session!.ScrollToRequested += id => scrolled = id;
        var hit = vm.GlobalResults.First(r => ReferenceEquals(r.Tab, first));
        vm.SelectGlobalHitCommand.Execute(hit);
        Assert.Equal(hit.Hit.AnchorId, scrolled);

        vm.GlobalSearchQuery = string.Empty;
        Assert.False(vm.HasGlobalResults);
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
