using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class RestartResumeTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static SessionManager NewManager(ScriptedAgentAdapter adapter, IEventStore store)
        => new(TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance);

    [Fact]
    public async Task History_and_sessions_survive_a_restart_and_resume_on_prompt()
    {
        // Shared store = durable disk across the "restart".
        var store = new InMemoryEventStore();
        var adapter1 = new ScriptedAgentAdapter();
        adapter1.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("first answer")));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        string sessionId;
        await using (var manager = NewManager(adapter1, store))
        {
            var info = await manager.OpenSessionAsync("scripted", "/tmp/work");
            sessionId = info.SessionId;
            await manager.PromptAsync(sessionId, [new TextContent("hello")]);
            await WaitFor(async () => (await manager.GetSnapshotAsync(sessionId, 0)).HeadSequence >= 3);
        }

        // ---- restart: a brand new manager over the same store, no live sessions ----
        var adapter2 = new ScriptedAgentAdapter();
        adapter2.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("second answer")));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };
        await using var resumed = NewManager(adapter2, store);
        await resumed.RestoreAsync();

        // History replays immediately for the dormant, restored session.
        var snapshot = await resumed.GetSnapshotAsync(sessionId, 0);
        Assert.Contains(snapshot.Events.OfType<MessageChunkEvent>(), m => (m.Content as TextContent)?.Text == "first answer");

        // Prompting re-attaches the agent and continues, appending a reconnect notice + the new turn.
        await resumed.PromptAsync(sessionId, [new TextContent("continue")]);
        await WaitFor(async () =>
        {
            var s = await resumed.GetSnapshotAsync(sessionId, 0);
            return s.Events.OfType<MessageChunkEvent>().Any(m => (m.Content as TextContent)?.Text == "second answer");
        });

        var after = await resumed.GetSnapshotAsync(sessionId, 0);
        Assert.Contains(after.Events.OfType<NoticeEvent>(), n => n.Message.Contains("Reconnected"));
    }

    [Fact]
    public async Task Unknown_session_after_restart_is_rejected()
    {
        var store = new InMemoryEventStore();
        await using var manager = NewManager(new ScriptedAgentAdapter(), store);
        await manager.RestoreAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetSnapshotAsync("nope", 0));
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
