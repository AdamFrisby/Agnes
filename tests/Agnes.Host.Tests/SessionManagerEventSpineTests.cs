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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
}
