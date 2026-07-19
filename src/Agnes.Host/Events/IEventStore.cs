using Agnes.Abstractions;

namespace Agnes.Host.Events;

/// <summary>Durable metadata for a session, so it can be re-opened (and its agent resumed) after a host restart.</summary>
public sealed record SessionRecord(
    string SessionId,
    string AdapterId,
    string WorkingDirectory,
    string? AgentSessionId,
    bool UseWorktree,
    bool SkipPermissions,
    bool Sandboxed,
    DateTimeOffset CreatedAt);

/// <summary>
/// Append-only, per-session event log. Assigns a monotonic sequence to each event and
/// supports snapshot+tail replay — the mechanism behind unlimited scrollback and
/// consistent multi-client sync. Also persists a small session catalog so the host can
/// restore sessions across a restart.
/// </summary>
public interface IEventStore
{
    /// <summary>Appends an event, stamping it with the next sequence and a timestamp.</summary>
    Task<SessionEvent> AppendAsync(string sessionId, SessionEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Returns events with sequence &gt; <paramref name="sinceSequence"/>, in order.</summary>
    Task<IReadOnlyList<SessionEvent>> ReadSinceAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default);

    /// <summary>Returns the highest assigned sequence for a session (0 if none).</summary>
    Task<long> GetHeadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Persists (upserts) a session's catalog record.</summary>
    Task SaveSessionAsync(SessionRecord record, CancellationToken cancellationToken = default);

    /// <summary>Lists all catalogued sessions (for restore on startup).</summary>
    Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default);
}
