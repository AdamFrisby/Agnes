using Agnes.Abstractions;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
using Agnes.Client.Simulation;
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
}

public class SessionStateStoreTests
{
    [Fact]
    public void Saves_and_loads_open_tabs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json");
        new SessionStateStore(path).Save(
        [
            new SessionDescriptor("sim://demo", "tok", "sim-1", "opencode", "OpenCode"),
            new SessionDescriptor("sim://demo", "tok", "sim-2", "codex", "Codex"),
        ]);

        var loaded = new SessionStateStore(path).Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("sim-2", loaded[1].SessionId);
        Assert.Equal("OpenCode", loaded[0].Title);
    }
}

public class MainWindowRestoreTests
{
    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => DocumentDock(vm).VisibleDockables!.OfType<SessionDocument>();

    [Fact]
    public async Task Restore_recreates_and_reconnects_saved_tabs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json");
        var store = new SessionStateStore(path);
        store.Save(
        [
            new SessionDescriptor("sim://demo", "", "sim-a", "opencode", "OpenCode"),
            new SessionDescriptor("sim://demo", "", "sim-b", "codex", "Codex"),
        ]);

        var vm = new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance, store);
        await vm.RestoreAsync();

        Assert.Equal(2, Tabs(vm).Count());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Tabs(vm).Any(d => d.Session is null))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }

        Assert.All(Tabs(vm), d => Assert.NotNull(d.Session));
    }

    [Fact]
    public async Task Empty_state_opens_a_single_fresh_tab()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json");
        var vm = new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance, new SessionStateStore(path));

        await vm.RestoreAsync();

        Assert.Single(Tabs(vm));
    }
}
