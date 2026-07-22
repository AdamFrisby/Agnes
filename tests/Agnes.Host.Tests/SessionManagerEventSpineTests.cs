using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>A plugin bound to the host event spine can veto or rewrite a real action — a prompt — before it
/// reaches the agent (see .ideas/00d-event-spine-and-ui-extensibility.md, AC4).</summary>
public class SessionManagerEventSpineTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private sealed class CancelPrompt : IEventInterceptor<BeforePromptEvent>
    {
        public ValueTask InterceptAsync(BeforePromptEvent evt, CancellationToken ct = default)
        {
            evt.Cancel("policy");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RewritePrompt : IEventInterceptor<BeforePromptEvent>
    {
        public ValueTask InterceptAsync(BeforePromptEvent evt, CancellationToken ct = default)
        {
            evt.Content = [new TextContent("REWRITTEN")];
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // generous for loaded CI runners
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private static (SessionManager Manager, ScriptedAgentAdapter Adapter, EventBus Bus) NewManager()
    {
        var adapter = new ScriptedAgentAdapter();
        var bus = new EventBus();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            eventBus: bus);
        return (manager, adapter, bus);
    }

    [Fact]
    public async Task An_interceptor_can_veto_a_prompt_so_the_agent_never_sees_it()
    {
        var (manager, adapter, bus) = NewManager();
        await using var _ = manager;
        IReadOnlyList<ContentBlock>? received = null;
        adapter.Session.OnPrompt = (c, s) => { received = c; s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };
        bus.Intercept(new CancelPrompt());

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("hello")]);

        Assert.Null(received); // vetoed before it reached the agent
        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        Assert.Contains(snapshot.Events, e => e is NoticeEvent n && n.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_interceptor_can_rewrite_a_prompt_before_it_reaches_the_agent()
    {
        var (manager, adapter, bus) = NewManager();
        await using var _ = manager;
        IReadOnlyList<ContentBlock>? received = null;
        adapter.Session.OnPrompt = (c, s) => { received = c; s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };
        bus.Intercept(new RewritePrompt());

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("hello")]);

        await WaitForAsync(() => received is not null);
        Assert.NotNull(received);
        Assert.Equal("REWRITTEN", Assert.IsType<TextContent>(received!.Single()).Text);
    }

    [Fact]
    public async Task With_no_interceptors_a_prompt_reaches_the_agent_unchanged()
    {
        var (manager, adapter, _) = NewManager();
        await using var _d = manager;
        IReadOnlyList<ContentBlock>? received = null;
        adapter.Session.OnPrompt = (c, s) => { received = c; s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("hello")]);

        await WaitForAsync(() => received is not null);
        Assert.NotNull(received);
        Assert.Equal("hello", Assert.IsType<TextContent>(received!.Single()).Text);
    }

    // ---- session lifecycle (open / stop / fork) ----

    private sealed class Cancel<TEvent>(string reason) : IEventInterceptor<TEvent> where TEvent : CancelableEvent
    {
        public ValueTask InterceptAsync(TEvent evt, CancellationToken ct = default) { evt.Cancel(reason); return ValueTask.CompletedTask; }
    }

    private sealed class Record<TEvent>(Action<TEvent> body) : IEventObserver<TEvent> where TEvent : IAgnesEvent
    {
        public ValueTask ObserveAsync(TEvent evt, CancellationToken ct = default) { body(evt); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task An_interceptor_can_veto_opening_a_session()
    {
        var (manager, _, bus) = NewManager();
        await using var _d = manager;
        bus.Intercept(new Cancel<BeforeSessionOpenEvent>("policy"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false));
    }

    [Fact]
    public async Task Opening_a_session_emits_a_SessionOpened_observe_event()
    {
        var (manager, _, bus) = NewManager();
        await using var _d = manager;
        var opened = new List<string>();
        bus.Observe(new Record<SessionOpenedEvent>(e => opened.Add(e.SessionId)));

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        Assert.Contains(info.SessionId, opened);
    }

    [Fact]
    public async Task Vetoing_a_stop_leaves_the_session_running_and_emits_no_stopped_event()
    {
        var (manager, _, bus) = NewManager();
        await using var _d = manager;
        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        var stopped = new List<string>();
        bus.Observe(new Record<SessionStoppedEvent>(e => stopped.Add(e.SessionId)));
        bus.Intercept(new Cancel<BeforeSessionStopEvent>("keep"));

        await manager.StopSessionAsync(info.SessionId);

        Assert.Empty(stopped); // the stop was vetoed
    }

    [Fact]
    public async Task A_non_vetoed_stop_emits_the_stopped_event()
    {
        var (manager, _, bus) = NewManager();
        await using var _d = manager;
        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        var stopped = new List<string>();
        bus.Observe(new Record<SessionStoppedEvent>(e => stopped.Add(e.SessionId)));

        await manager.StopSessionAsync(info.SessionId);

        Assert.Contains(info.SessionId, stopped);
    }

    [Fact]
    public async Task An_interceptor_can_veto_a_fork()
    {
        var (manager, _, bus) = NewManager();
        await using var _d = manager;
        var root = Path.Combine(Path.GetTempPath(), "agnes-forkveto-" + Guid.NewGuid().ToString("n"));
        var src = Path.Combine(root, "Repo1");
        Directory.CreateDirectory(src);
        try
        {
            var info = await manager.OpenSessionAsync("scripted", src, useSandbox: false);
            bus.Intercept(new Cancel<BeforeSessionForkEvent>("no forking"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ForkSessionAsync(info.SessionId, Path.Combine(root, "Repo2"), copySandbox: false));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
