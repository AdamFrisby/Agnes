using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The session catalogue a client asks for after pairing ("what's already running here?"): every session
/// the host knows about, with the coarse state, waiting-on-a-human count and last-activity stamp a client
/// needs to decide what to rejoin. It is an aggregation over state the host already holds — asking must
/// neither open nor resume anything.
/// </summary>
public class SessionCatalogueTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static SessionManager NewManager(ScriptedAgentAdapter adapter, IEventStore store)
        => new(TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance);

    private static PermissionRequestedEvent Requested(string requestId, string toolCallId, string title)
        => new(requestId, toolCallId, title, [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce)]);

    // Emits a scripted set of events for one prompt and waits until they're all persisted.
    private static async Task EmitAsync(SessionManager manager, ScriptedAgentAdapter adapter, IEventStore store, string sessionId, params SessionEvent[] events)
    {
        adapter.Session.OnPrompt = (_, s) =>
        {
            foreach (var e in events)
            {
                s.Emit(e);
            }

            return Task.FromResult(StopReason.EndTurn);
        };

        var before = await store.GetHeadAsync(sessionId);
        await manager.PromptAsync(sessionId, [new TextContent("go")]);
        var expected = before + 1 + events.Length;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await store.GetHeadAsync(sessionId) < expected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    // Waits for the session to stop reporting a turn in flight (the prompt returns before the host has
    // finished recording the turn's end).
    private static async Task SettleAsync(SessionManager manager, string sessionId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await manager.ListSessionSummariesAsync(cts.Token))
               .First(s => s.SessionId == sessionId).State == SessionRunState.Working)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task Lists_an_open_session_with_the_facts_a_client_needs_to_rejoin_it()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        var summary = Assert.Single(await manager.ListSessionSummariesAsync());
        Assert.Equal(info.SessionId, summary.SessionId);
        Assert.Equal("scripted", summary.AdapterId);
        Assert.Equal("/tmp/work", summary.WorkingDirectory);
        // Live but quiet: idle, and nothing waiting on a human.
        Assert.Equal(SessionRunState.Idle, summary.State);
        Assert.Equal(0, summary.OpenApprovals);
        Assert.False(summary.IsBlocked);
        Assert.NotNull(summary.StartedAt);
    }

    [Fact]
    public async Task Reports_the_sessions_that_are_waiting_on_a_human()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        // One request answered, two still open — only the unanswered ones count as blocking.
        await EmitAsync(manager, adapter, store, info.SessionId,
            Requested("req-answered", "tool-1", "Write a file"),
            new PermissionResolvedEvent("req-answered", "allow", PermissionOutcome.Allowed),
            Requested("req-open-1", "tool-2", "Run the tests"),
            Requested("req-open-2", "tool-3", "Delete a folder"),
            new TurnEndedEvent(StopReason.EndTurn));

        var summary = Assert.Single(await manager.ListSessionSummariesAsync());
        Assert.Equal(2, summary.OpenApprovals);
        Assert.True(summary.IsBlocked);
        Assert.NotNull(summary.LastActivityAt);

        // Blocked is not a run state — it's orthogonal. Once the turn finishes the session reads as Idle
        // while still holding both unanswered requests.
        await SettleAsync(manager, info.SessionId);
        var settled = Assert.Single(await manager.ListSessionSummariesAsync());
        Assert.Equal(SessionRunState.Idle, settled.State);
        Assert.Equal(2, settled.OpenApprovals);
    }

    [Fact]
    public async Task Head_sequence_and_last_activity_track_the_log()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        var before = Assert.Single(await manager.ListSessionSummariesAsync());

        await EmitAsync(manager, adapter, store, info.SessionId,
            new MessageChunkEvent(MessageRole.Assistant, new TextContent("done")));

        var after = Assert.Single(await manager.ListSessionSummariesAsync());
        // A session whose log is still empty honestly reports no activity rather than a made-up stamp;
        // once anything is appended, the stamp is the head event's.
        Assert.Null(before.LastActivityAt);
        Assert.True(after.HeadSequence > before.HeadSequence);
        Assert.NotNull(after.LastActivityAt);
    }

    [Fact]
    public async Task Listing_neither_opens_nor_resumes_anything()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        var head = await store.GetHeadAsync(info.SessionId);

        await manager.ListSessionSummariesAsync();
        await manager.ListSessionSummariesAsync();

        // No new events, and no second session conjured by asking.
        Assert.Equal(head, await store.GetHeadAsync(info.SessionId));
        Assert.Single(await manager.ListSessionSummariesAsync());
    }
}
