using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>Every inbound agent event is dispatched on the spine: plugins observe them with full typing
/// (SessionEvent : IAgnesEvent), and BeforeAgentEvent lets a plugin redact one from clients while it stays
/// in the log (see .ideas/00d-event-spine-and-ui-extensibility.md).</summary>
public class AgentEventBridgeTests
{
    private sealed class CollectingBroadcaster : ISessionBroadcaster
    {
        public List<SessionEvent> Published { get; } = [];
        public Task PublishAsync(string sessionId, SessionEvent @event) { lock (Published) { Published.Add(@event); } return Task.CompletedTask; }
    }

    private sealed class ObserveTool(List<string> sink) : IEventObserver<ToolCallEvent>
    {
        public ValueTask ObserveAsync(ToolCallEvent evt, CancellationToken ct = default) { lock (sink) { sink.Add(evt.ToolCallId); } return ValueTask.CompletedTask; }
    }

    // Redacts tool-call events from clients (still logged).
    private sealed class RedactToolCalls : IEventInterceptor<BeforeAgentEventEvent>
    {
        public ValueTask InterceptAsync(BeforeAgentEventEvent evt, CancellationToken ct = default)
        {
            if (evt.Event is ToolCallEvent) { evt.Cancel("redacted"); }
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) { cts.Token.ThrowIfCancellationRequested(); await Task.Delay(10, cts.Token); }
    }

    private static ToolCallEvent Tool(string id) => new(id, id, ToolKind.Execute, ToolCallStatus.Pending, [new TextContent(id)]);

    [Fact]
    public async Task A_plugin_can_observe_inbound_tool_calls_with_full_typing()
    {
        var adapter = new ScriptedAgentAdapter();
        var bus = new EventBus();
        var seen = new List<string>();
        bus.Observe(new ObserveTool(seen));
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);
        adapter.Session.OnPrompt = (_, s) => { s.Emit(Tool("tc-1")); s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("go")]);

        await WaitForAsync(() => seen.Contains("tc-1"));
    }

    [Fact]
    public async Task A_plugin_can_redact_an_event_from_clients_while_it_stays_in_the_log()
    {
        var adapter = new ScriptedAgentAdapter();
        var bus = new EventBus();
        bus.Intercept(new RedactToolCalls());
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster, NullLoggerFactory.Instance, eventBus: bus);
        adapter.Session.OnPrompt = (_, s) => { s.Emit(Tool("secret")); s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("go")]);
        await WaitForAsync(() => broadcaster.Published.OfType<TurnEndedEvent>().Any());

        // The tool call was NOT broadcast to clients...
        Assert.DoesNotContain(broadcaster.Published, e => e is ToolCallEvent);
        // ...but it IS in the durable log.
        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        Assert.Contains(snapshot.Events, e => e is ToolCallEvent t && t.ToolCallId == "secret");
    }
}
