using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class SessionManagerTests
{
    /// <summary>Records broadcast events, standing in for connected clients.</summary>
    private sealed class CollectingBroadcaster : ISessionBroadcaster
    {
        public ConcurrentQueue<(string SessionId, SessionEvent Event)> Published { get; } = new();

        public Task PublishAsync(string sessionId, SessionEvent @event)
        {
            Published.Enqueue((sessionId, @event));
            return Task.CompletedTask;
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

    [Fact]
    public async Task Prompt_records_user_event_then_streams_and_broadcasts_agent_events()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager([adapter], store, broadcaster, NullLoggerFactory.Instance);

        adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("Hi there")));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work");
        await manager.PromptAsync(info.SessionId, [new TextContent("hello")]);

        await WaitForAsync(() => broadcaster.Published.Count >= 3);

        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        Assert.Equal(3, snapshot.HeadSequence);
        Assert.Equal([1L, 2L, 3L], snapshot.Events.Select(e => e.Sequence));

        // Order: user prompt first, then assistant, then turn end.
        var user = Assert.IsType<MessageChunkEvent>(snapshot.Events[0]);
        Assert.Equal(MessageRole.User, user.Role);
        var assistant = Assert.IsType<MessageChunkEvent>(snapshot.Events[1]);
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.Equal("Hi there", ((TextContent)assistant.Content).Text);
        Assert.IsType<TurnEndedEvent>(snapshot.Events[2]);
    }

    [Fact]
    public async Task Late_subscriber_replays_full_history_via_snapshot()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager([adapter], store, broadcaster, NullLoggerFactory.Instance);

        adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("done")));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work");
        await manager.PromptAsync(info.SessionId, [new TextContent("go")]);
        await WaitForAsync(() => broadcaster.Published.Count >= 3);

        // A client that connects now (cursor 0) gets the entire history as a snapshot.
        var full = await manager.GetSnapshotAsync(info.SessionId, 0);
        Assert.Equal(3, full.Events.Count);

        // A client resuming from cursor 2 gets only what it missed.
        var tail = await manager.GetSnapshotAsync(info.SessionId, 2);
        Assert.Single(tail.Events);
        Assert.IsType<TurnEndedEvent>(tail.Events[0]);
    }

    [Fact]
    public async Task ListAgents_reports_registered_adapters()
    {
        var adapter = new ScriptedAgentAdapter();
        await using var manager = new SessionManager(
            [adapter], new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance);

        var agents = manager.ListAgents();
        var agent = Assert.Single(agents);
        Assert.Equal("scripted", agent.AdapterId);
        Assert.True(agent.Available);
    }

    [Fact]
    public async Task Fork_copies_the_working_folder_and_opens_a_new_session_there()
    {
        var adapter = new ScriptedAgentAdapter();
        await using var manager = new SessionManager(
            [adapter], new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance);

        var root = Path.Combine(Path.GetTempPath(), "agnes-forktest-" + Guid.NewGuid().ToString("n"));
        var src = Path.Combine(root, "Repo1");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "note.txt"), "hello fork");

        try
        {
            var source = await manager.OpenSessionAsync("scripted", src, useSandbox: false);

            // The proposed target is a numeral-incremented, non-existing sibling.
            var plan = manager.ProposeFork(source.SessionId);
            Assert.NotNull(plan);
            Assert.Equal(src, plan!.SourceDirectory);
            Assert.Equal(Path.Combine(root, "Repo2"), plan.ProposedDirectory);
            Assert.False(plan.CanCopySandbox); // no sandbox provider configured

            var fork = await manager.ForkSessionAsync(source.SessionId, plan.ProposedDirectory, copySandbox: false);

            Assert.NotEqual(source.SessionId, fork.SessionId);
            Assert.Equal(plan.ProposedDirectory, fork.WorkingDirectory);
            Assert.Equal("scripted", fork.AdapterId);
            // The working folder was copied faithfully.
            Assert.Equal("hello fork", await File.ReadAllTextAsync(Path.Combine(plan.ProposedDirectory, "note.txt")));
            // Both sessions are live and independent.
            Assert.NotNull(await manager.GetSnapshotAsync(fork.SessionId, 0));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ProposeFork_returns_null_for_an_unknown_session()
    {
        var adapter = new ScriptedAgentAdapter();
        await using var manager = new SessionManager(
            [adapter], new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance);
        Assert.Null(manager.ProposeFork("does-not-exist"));
    }
}
