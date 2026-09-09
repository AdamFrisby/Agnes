using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// What a failing event store costs the session that hits it. The pump reads the agent's stream in one loop,
/// so an exception thrown while recording an event used to unwind out of that loop and signal a fault —
/// which restarts the CLI and resumes it with <c>session/load</c>, replaying the whole conversation. A
/// SQLite lock lasting milliseconds therefore cost a live session its agent, and the replay it forced grew
/// with every restart. The storage bug behind those locks is fixed separately
/// (<see cref="EventStoreConcurrencyTests"/>); this is the blast radius, which is worth containing on its own.
/// </summary>
public class EventPumpResilienceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    /// <summary>An event store that throws for a scripted number of appends, then behaves.</summary>
    private sealed class FlakyEventStore(int failures) : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        private int _remaining = failures;

        public int Attempts;

        public Task<SessionEvent> AppendAsync(string sessionId, SessionEvent @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            if (Interlocked.Decrement(ref _remaining) >= 0)
            {
                // The shape the live host actually threw: SQLITE_LOCKED surfacing from the append.
                throw new InvalidOperationException("SQLite Error 6: 'database table is locked: events'.");
            }

            return _inner.AppendAsync(sessionId, @event, cancellationToken);
        }

        public Task<IReadOnlyList<SessionEvent>> ReadSinceAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default)
            => _inner.ReadSinceAsync(sessionId, sinceSequence, cancellationToken);

        public Task<long> GetHeadAsync(string sessionId, CancellationToken cancellationToken = default)
            => _inner.GetHeadAsync(sessionId, cancellationToken);

        public Task<int> PruneEventsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => _inner.PruneEventsBeforeAsync(cutoff, cancellationToken);

        public Task SaveSessionAsync(SessionRecord record, CancellationToken cancellationToken = default)
            => _inner.SaveSessionAsync(record, cancellationToken);

        public Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => _inner.ListSessionsAsync(cancellationToken);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition never became true");
    }

    [Fact]
    public async Task A_transient_append_failure_costs_one_event_not_the_agent()
    {
        var agent = new ScriptedAgentSession();
        var store = new FlakyEventStore(failures: 1);
        var faulted = 0;

        await using var session = new HostSession(
            "s1", "scripted", "/work", agent, store, new NullBroadcaster(), NullLogger.Instance)
        {
            Faulted = () => Interlocked.Increment(ref faulted),
        };

        agent.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("lost to the lock")));
        agent.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("after the lock")));

        await WaitForAsync(() => Volatile.Read(ref store.Attempts) >= 2);

        // The pump kept reading: the event after the failure was recorded normally.
        var recorded = await store.ReadSinceAsync("s1", 0);
        Assert.Equal("after the lock", Assert.IsType<TextContent>(Assert.IsType<MessageChunkEvent>(Assert.Single(recorded)).Content).Text);

        // And crucially the agent was never torn down and replayed over one lock.
        Assert.Equal(0, Volatile.Read(ref faulted));
    }

    [Fact]
    public async Task A_store_that_never_recovers_still_faults_the_session()
    {
        var agent = new ScriptedAgentSession();
        var store = new FlakyEventStore(failures: int.MaxValue);
        var faulted = 0;

        await using var session = new HostSession(
            "s1", "scripted", "/work", agent, store, new NullBroadcaster(), NullLogger.Instance)
        {
            Faulted = () => Interlocked.Increment(ref faulted),
        };

        // Absorbing a blip must not mean recording nothing while looking healthy: a storage layer that is
        // simply broken has to escalate, so the host can restart and resume rather than run blind.
        for (var i = 0; i < 40; i++)
        {
            agent.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent($"event {i}")));
        }

        await WaitForAsync(() => Volatile.Read(ref faulted) > 0);
        Assert.Equal(1, Volatile.Read(ref faulted));
    }
}
