using Agnes.Abstractions;

namespace Agnes.Host.Events;

/// <summary>
/// Append-only, per-session event log. Assigns a monotonic sequence to each event and
/// supports snapshot+tail replay — the mechanism behind unlimited scrollback and
/// consistent multi-client sync.
/// </summary>
public interface IEventStore
{
    /// <summary>Appends an event, stamping it with the next sequence and a timestamp.</summary>
    Task<SessionEvent> AppendAsync(string sessionId, SessionEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Returns events with sequence &gt; <paramref name="sinceSequence"/>, in order.</summary>
    Task<IReadOnlyList<SessionEvent>> ReadSinceAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default);

    /// <summary>Returns the highest assigned sequence for a session (0 if none).</summary>
    Task<long> GetHeadAsync(string sessionId, CancellationToken cancellationToken = default);
}
