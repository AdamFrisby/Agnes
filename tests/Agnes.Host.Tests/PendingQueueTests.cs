using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The host-side pending-message queue + send policies (sessions/03): one ordered queue per session, its
/// mutations rippling to clients as PendingQueueEvent snapshots on the event log. Driven with the scripted
/// in-memory agent + InMemoryEventStore, offline. A turn is kept "active" by having the scripted agent's
/// prompt handler NOT emit a TurnEndedEvent (the host sets busy on prompt and clears it only on that event);
/// the test ends a turn explicitly by emitting one.
/// </summary>
public class PendingQueueTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static SessionManager NewManager(ScriptedAgentAdapter adapter, IEventStore store)
        => new(TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance);

    private static string Text(IReadOnlyList<ContentBlock> content)
        => string.Concat(content.OfType<TextContent>().Select(t => t.Text));

    // A scripted agent that records every prompt it receives (in order) and never ends its own turn, so the
    // test controls turn boundaries. Also counts cancels so "did we interrupt?" is observable.
    private sealed class Recorder
    {
        public readonly List<string> Prompts = [];
        public int Cancels;
        private readonly object _gate = new();

        public void Attach(ScriptedAgentAdapter adapter)
        {
            adapter.Session.OnPrompt = (content, _) =>
            {
                lock (_gate)
                {
                    Prompts.Add(Text(content));
                }

                return Task.FromResult(StopReason.EndTurn);
            };
            adapter.Session.OnCancel = () =>
            {
                Interlocked.Increment(ref Cancels);
                return Task.CompletedTask;
            };
        }

        public string[] Snapshot()
        {
            lock (_gate)
            {
                return [.. Prompts];
            }
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition().ConfigureAwait(false))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }
    }

    private static Task WaitForPromptsAsync(Recorder rec, params string[] expected)
        => WaitForAsync(() => Task.FromResult(rec.Snapshot().SequenceEqual(expected)));

    private static async Task<PendingQueueEvent?> LatestQueueAsync(IEventStore store, string sessionId)
    {
        var events = await store.ReadSinceAsync(sessionId, 0).ConfigureAwait(false);
        return events.OfType<PendingQueueEvent>().LastOrDefault();
    }

    // Waits until the session's latest queue snapshot has exactly the given queued texts (in order).
    private static Task WaitForQueueAsync(IEventStore store, string sessionId, params string[] queued)
        => WaitForAsync(async () =>
        {
            var q = await LatestQueueAsync(store, sessionId).ConfigureAwait(false);
            return q is not null && q.Queue.Select(m => Text(m.Content)).SequenceEqual(queued);
        });

    [Fact]
    public async Task Default_policy_queues_during_active_turn_and_auto_sends_after_turn_end()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);
        var rec = new Recorder();
        rec.Attach(adapter);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/pq", useSandbox: false);

        // Idle send under the default QueueInAgent policy goes out immediately and starts a turn.
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("first")]);
        await WaitForPromptsAsync(rec, "first");

        // A second send while that turn is active is queued — it must NOT interrupt the running turn.
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("second")]);
        await WaitForQueueAsync(store, info.SessionId, "second");
        Assert.Equal(["first"], rec.Snapshot());
        Assert.Equal(0, rec.Cancels);

        // Ending the turn auto-sends the head of the queue, in order.
        adapter.Session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        await WaitForPromptsAsync(rec, "first", "second");
        Assert.Equal(0, rec.Cancels); // never interrupted

        var queue = await LatestQueueAsync(store, info.SessionId);
        Assert.NotNull(queue);
        Assert.Empty(queue!.Queue); // drained
    }

    [Fact]
    public async Task Reorder_and_remove_change_the_delivered_order()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);
        var rec = new Recorder();
        rec.Attach(adapter);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/pq", useSandbox: false);

        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("first")]);
        await WaitForPromptsAsync(rec, "first");

        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("a")]);
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("b")]);
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("c")]);
        await WaitForQueueAsync(store, info.SessionId, "a", "b", "c");

        var queue = await LatestQueueAsync(store, info.SessionId);
        var idA = queue!.Queue.First(m => Text(m.Content) == "a").Id;
        var idB = queue.Queue.First(m => Text(m.Content) == "b").Id;

        await manager.ReorderPendingMessageAsync(info.SessionId, idA, 2); // a,b,c -> b,c,a
        await WaitForQueueAsync(store, info.SessionId, "b", "c", "a");

        await manager.RemovePendingMessageAsync(info.SessionId, idB); // drop b -> c,a
        await WaitForQueueAsync(store, info.SessionId, "c", "a");

        // Drain: two turn-ends deliver c then a, in the reordered order.
        adapter.Session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        await WaitForPromptsAsync(rec, "first", "c");
        adapter.Session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        await WaitForPromptsAsync(rec, "first", "c", "a");
    }

    [Fact]
    public async Task Send_now_interrupts_the_turn_and_delivers_that_message_ahead_of_the_rest()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);
        var rec = new Recorder();
        rec.Attach(adapter);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/pq", useSandbox: false);

        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("first")]);
        await WaitForPromptsAsync(rec, "first");

        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("second")]);
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("third")]);
        await WaitForQueueAsync(store, info.SessionId, "second", "third");

        var queue = await LatestQueueAsync(store, info.SessionId);
        var idThird = queue!.Queue.First(m => Text(m.Content) == "third").Id;

        // Send "third" now: interrupts the running turn (cancel-then-resend) and delivers it ahead of "second".
        await manager.SendPendingNowAsync(info.SessionId, idThird);
        await WaitForPromptsAsync(rec, "first", "third");
        Assert.Equal(1, rec.Cancels);

        // "second" is still queued, ahead of nothing else.
        await WaitForQueueAsync(store, info.SessionId, "second");
    }

    [Fact]
    public async Task Interrupt_and_send_policy_cancels_the_active_turn_and_sends_immediately()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);
        var rec = new Recorder();
        rec.Attach(adapter);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/pq", useSandbox: false);
        await manager.SetSendPolicyAsync(info.SessionId, SendPolicy.InterruptAndSend);

        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("first")]);
        await WaitForPromptsAsync(rec, "first");

        // Under InterruptAndSend, a send while busy cancels the current turn and sends now (no queueing).
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("second")]);
        await WaitForPromptsAsync(rec, "first", "second");
        Assert.Equal(1, rec.Cancels);

        var queue = await LatestQueueAsync(store, info.SessionId);
        Assert.True(queue is null || queue.Queue.Count == 0); // nothing was ever queued
    }

    [Fact]
    public async Task Pending_until_ready_policy_never_auto_sends_even_when_idle()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);
        var rec = new Recorder();
        rec.Attach(adapter);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/pq", useSandbox: false);
        await manager.SetSendPolicyAsync(info.SessionId, SendPolicy.PendingUntilReady);

        // Even though the agent is idle, the message is held — not sent.
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("held")]);
        await WaitForQueueAsync(store, info.SessionId, "held");
        Assert.Empty(rec.Snapshot());

        var queue = await LatestQueueAsync(store, info.SessionId);
        var id = queue!.Queue.Single().Id;

        // Only an explicit send-now delivers it.
        await manager.SendPendingNowAsync(info.SessionId, id);
        await WaitForPromptsAsync(rec, "held");
    }

    [Fact]
    public async Task Queue_snapshot_is_appended_to_the_log_so_a_replaying_client_sees_it()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);
        var rec = new Recorder();
        rec.Attach(adapter);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/pq", useSandbox: false);

        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("first")]);
        await WaitForPromptsAsync(rec, "first");
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("q1")]);
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("q2")]);
        await WaitForQueueAsync(store, info.SessionId, "q1", "q2");

        // A second client "joins" by replaying the durable log from 0 (exactly what Subscribe/GetSnapshot
        // does); the latest PendingQueueEvent carries the same queue, in order — no bespoke sync channel.
        var replayed = await store.ReadSinceAsync(info.SessionId, 0);
        var latest = replayed.OfType<PendingQueueEvent>().Last();
        Assert.Equal(["q1", "q2"], latest.Queue.Select(m => Text(m.Content)));
        Assert.Empty(latest.Discarded);
    }

    [Fact]
    public async Task Session_teardown_moves_queued_messages_to_the_discarded_list()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);
        var rec = new Recorder();
        rec.Attach(adapter);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/pq", useSandbox: false);

        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("first")]);
        await WaitForPromptsAsync(rec, "first");
        await manager.EnqueuePendingMessageAsync(info.SessionId, [new TextContent("undelivered")]);
        await WaitForQueueAsync(store, info.SessionId, "undelivered");

        // Tearing the session down while the queue is non-empty must not silently drop the message: it moves
        // to the discarded list, surfaced via a final PendingQueueEvent.
        await manager.StopSessionAsync(info.SessionId);

        var latest = await LatestQueueAsync(store, info.SessionId);
        Assert.NotNull(latest);
        Assert.Empty(latest!.Queue);
        Assert.Equal(["undelivered"], latest.Discarded.Select(m => Text(m.Content)));
    }
}
