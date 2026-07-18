using Agnes.Abstractions;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

public class SimulatedHostTests
{
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task Connect_list_open_subscribe_and_stream_a_response()
    {
        var host = new SimulatedHost();
        await host.ConnectAsync();
        Assert.Equal(AgnesConnectionState.Connected, host.State);

        var agents = await host.ListAgentsAsync();
        Assert.Contains(agents, a => a.AdapterId == "opencode");

        var info = await host.OpenSessionAsync("opencode", "/tmp/agnes");
        var view = await host.SubscribeAsync(info.SessionId);
        Assert.Contains(view.Events, e => e is MessageChunkEvent); // greeting

        await host.PromptAsync(info.SessionId, [new TextContent("hello there")]);
        await WaitAsync(() => view.Events.OfType<TurnEndedEvent>().Count() >= 2);

        Assert.Contains(view.Events, e => e is MessageChunkEvent { Role: MessageRole.User });
        Assert.Contains(view.Events, e => e is MessageChunkEvent { Role: MessageRole.Assistant });
    }

    [Fact]
    public async Task Subscribe_to_unknown_session_fabricates_restored_history()
    {
        var host = new SimulatedHost();
        await host.ConnectAsync();

        var view = await host.SubscribeAsync("sim-restored-xyz");

        Assert.NotEmpty(view.Events);
        Assert.Contains(view.Events, e => e is ToolCallEvent); // restored history includes a tool call
    }

    [Fact]
    public async Task Permission_request_streams_and_resolves()
    {
        var host = new SimulatedHost();
        await host.ConnectAsync();
        var info = await host.OpenSessionAsync("opencode", "/tmp/agnes");
        var view = await host.SubscribeAsync(info.SessionId);

        await host.PromptAsync(info.SessionId, [new TextContent("please delete the build folder")]);
        await WaitAsync(() => view.Events.OfType<PermissionRequestedEvent>().Any());

        var request = view.Events.OfType<PermissionRequestedEvent>().First();
        await host.RespondPermissionAsync(info.SessionId, request.RequestId, "allow-once");
        await WaitAsync(() => view.Events.OfType<PermissionResolvedEvent>().Any());

        Assert.Equal(PermissionOutcome.Allowed, view.Events.OfType<PermissionResolvedEvent>().First().Outcome);
    }

    [Fact]
    public void Usage_info_computes_context_and_quota_percentages()
    {
        var u = new UsageInfo(ContextUsed: 50_000, ContextMax: 200_000, Used: 400, Limit: 5_000);
        Assert.True(u.HasContext);
        Assert.Equal(25, u.ContextPercent);
        Assert.True(u.HasQuota);
        Assert.Equal(8, u.QuotaPercent);

        var empty = new UsageInfo();
        Assert.False(empty.HasContext);
        Assert.False(empty.HasQuota);
    }

    [Fact]
    public async Task Simulated_host_reports_structured_usage_that_grows_with_prompts()
    {
        var host = new SimulatedHost();
        await host.ConnectAsync();

        Assert.NotNull(host.Usage);
        Assert.True(host.Usage!.HasContext);
        Assert.True(host.Usage.HasQuota);

        var before = host.Usage.ContextUsed;
        var info = await host.OpenSessionAsync("opencode", "/tmp/agnes");
        await host.PromptAsync(info.SessionId, [new TextContent("hello world")]);

        Assert.True(host.Usage!.ContextUsed > before);
    }

    [Fact]
    public async Task Cancel_stops_a_running_turn()
    {
        var host = new SimulatedHost();
        await host.ConnectAsync();
        var info = await host.OpenSessionAsync("opencode", "/tmp/agnes");
        var view = await host.SubscribeAsync(info.SessionId);

        // A long, word-by-word streaming response we can interrupt mid-turn.
        await host.PromptAsync(info.SessionId, [new TextContent("explain the protocol in detail")]);
        await WaitAsync(() => view.Events.OfType<MessageChunkEvent>().Any(m => m.Role == MessageRole.Assistant));

        await host.CancelAsync(info.SessionId);
        await WaitAsync(() => view.Events.OfType<TurnEndedEvent>().Any(e => e.Reason == StopReason.Cancelled));

        // Streaming must actually stop: no further events after cancellation settles.
        var count = view.Events.Count;
        await Task.Delay(200);
        Assert.Equal(count, view.Events.Count);
    }
}

public class SessionStateStoreTests
{
    [Fact]
    public void Saves_and_loads_open_tabs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json");
        new SessionStateStore(path).Save(
        [
            new SessionDescriptor("Simulated host", "sim://demo", "tok", "sim-1", "opencode", "OpenCode"),
            new SessionDescriptor("Simulated host", "sim://demo", "tok", "sim-2", "codex", "Codex"),
        ]);

        var loaded = new SessionStateStore(path).Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("sim-2", loaded[1].SessionId);
        Assert.Equal("OpenCode", loaded[0].Title);
    }

    [Fact]
    public void Permission_policy_persists_and_matches_per_host_and_tool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-policy-{Guid.NewGuid():n}.json");
        var store = new FilePermissionPolicy(path);

        store.Remember("sim://demo", ToolKind.Read, allow: true);
        store.Remember("sim://demo", ToolKind.Delete, allow: false);

        var reloaded = new FilePermissionPolicy(path);
        Assert.True(reloaded.Decide("sim://demo", ToolKind.Read));
        Assert.False(reloaded.Decide("sim://demo", ToolKind.Delete));
        Assert.Null(reloaded.Decide("sim://demo", ToolKind.Edit));   // no rule
        Assert.Null(reloaded.Decide("other://host", ToolKind.Read)); // different host

        reloaded.Forget("sim://demo", ToolKind.Read);
        Assert.Null(new FilePermissionPolicy(path).Decide("sim://demo", ToolKind.Read));
    }
}

public class MainWindowRestoreTests
{
    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => DocumentDock(vm).VisibleDockables!.OfType<SessionDocument>();

    private static MainWindowViewModel NewVm(string tabsPath, string hostsPath)
        => new(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(tabsPath), new HostRegistryStore(hostsPath));

    /// <summary>Drives a fresh tab through the host → agent flow to a live session.</summary>
    private static async Task OpenSessionAsync(SessionDocument tab)
    {
        await WaitAsync(() => tab.Hosts is { Count: > 0 });
        tab.Hosts!.First().Select.Execute(null); // simulated host
        await WaitAsync(() => tab.Agents is { Count: > 0 });
        tab.Agents!.First(a => a.AdapterId == "opencode").Open.Execute(null);
        await WaitAsync(() => tab.Session is not null);
    }

    [Fact]
    public async Task Restore_recreates_and_reconnects_saved_tabs()
    {
        var (tabs, hosts) = TempPaths();
        var store = new SessionStateStore(tabs);
        store.Save(
        [
            new SessionDescriptor("Simulated host", "sim://demo", "", "sim-a", "opencode", "OpenCode"),
            new SessionDescriptor("Simulated host", "sim://demo", "", "sim-b", "codex", "Codex"),
        ]);

        var vm = new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance, store, new HostRegistryStore(hosts));
        await vm.RestoreAsync();

        Assert.Equal(2, Tabs(vm).Count());
        await WaitAsync(() => Tabs(vm).All(d => d.Session is not null));
        Assert.All(Tabs(vm), d => Assert.NotNull(d.Session));
    }

    [Fact]
    public async Task Empty_state_opens_a_single_fresh_tab_on_the_host_picker()
    {
        var (tabs, hosts) = TempPaths();
        var vm = NewVm(tabs, hosts);

        await vm.RestoreAsync();

        var tab = Assert.Single(Tabs(vm));
        Assert.True(tab.IsPickingHost);
        await WaitAsync(() => tab.Hosts is { Count: > 0 }); // built-in simulated host is offered
    }

    [Fact]
    public async Task Relaunch_restores_session_without_clobbering_saved_state()
    {
        var (tabs, hosts) = TempPaths();

        // First launch: pick host → agent to persist a session.
        var vm1 = NewVm(tabs, hosts);
        await vm1.RestoreAsync();
        await OpenSessionAsync(Tabs(vm1).Single());

        Assert.Single(new SessionStateStore(tabs).Load()); // persisted

        // Relaunch: a fresh VM sharing the same stores restores the tab, not wipes it.
        var vm2 = NewVm(tabs, hosts);
        await vm2.RestoreAsync();
        var restored = Tabs(vm2).Single();
        Assert.NotNull(restored.Descriptor);
        Assert.Equal("Simulated host", restored.HostName);
        await WaitAsync(() => restored.Session is not null);
    }

    private static (string Tabs, string Hosts) TempPaths()
        => (Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-hosts-{Guid.NewGuid():n}.json"));

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
