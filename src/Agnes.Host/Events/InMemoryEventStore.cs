using System.Collections.Concurrent;
using Agnes.Abstractions;

namespace Agnes.Host.Events;

/// <summary>In-memory event store. Default store; also used by tests.</summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, SessionLog> _logs = new();

    public Task<SessionEvent> AppendAsync(string sessionId, SessionEvent @event, CancellationToken cancellationToken = default)
    {
        var log = _logs.GetOrAdd(sessionId, _ => new SessionLog());
        lock (log.Gate)
        {
            var stamped = @event with { Sequence = log.Events.Count + 1, Timestamp = DateTimeOffset.UtcNow };
            log.Events.Add(stamped);
            return Task.FromResult(stamped);
        }
    }

    public Task<IReadOnlyList<SessionEvent>> ReadSinceAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
        {
            return Task.FromResult<IReadOnlyList<SessionEvent>>([]);
        }

        lock (log.Gate)
        {
            IReadOnlyList<SessionEvent> slice = log.Events.Where(e => e.Sequence > sinceSequence).ToArray();
            return Task.FromResult(slice);
        }
    }

    public Task<long> GetHeadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
        {
            return Task.FromResult(0L);
        }

        lock (log.Gate)
        {
            return Task.FromResult((long)log.Events.Count);
        }
    }

    private readonly ConcurrentDictionary<string, SessionRecord> _catalog = new();

    public Task SaveSessionAsync(SessionRecord record, CancellationToken cancellationToken = default)
    {
        _catalog[record.SessionId] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionRecord>>(_catalog.Values.ToArray());

    public Task<int> PruneEventsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var log in _logs.Values)
        {
            lock (log.Gate)
            {
                removed += log.Events.RemoveAll(e => e.Timestamp < cutoff);
            }
        }

        return Task.FromResult(removed);
    }

    private sealed class SessionLog
    {
        public object Gate { get; } = new();
        public List<SessionEvent> Events { get; } = [];
    }
}
