namespace Agnes.Abstractions;

/// <summary>
/// How much existing history to index when memory search is first enabled (see
/// .ideas/ops/02-memory-search.md's backfill scope). Backfill runs off the hot path so it never
/// blocks the host from handling live work while it catches up.
/// </summary>
public enum MemoryBackfillScope
{
    /// <summary>Index nothing historical — only events appended from now on.</summary>
    NewOnly,

    /// <summary>Index events recorded within the last 30 days.</summary>
    Last30Days,

    /// <summary>Index the full event log.</summary>
    AllHistory,
}

/// <summary>
/// One hit from a memory search: the session and log sequence the match came from, a highlighted
/// <see cref="Snippet"/> of the matching text, and when the matched event was recorded — enough for a
/// client to render the result and jump straight to that point in the session's transcript.
/// </summary>
public sealed record MemorySearchResult(string SessionId, long Sequence, string Snippet, DateTimeOffset Timestamp);

/// <summary>Query knobs for <see cref="IMemoryIndexProvider.SearchAsync"/>.</summary>
/// <param name="Limit">Maximum number of results to return (ranked best-first).</param>
/// <param name="SessionId">When set, restricts results to a single session; null searches every session.</param>
public sealed record MemorySearchOptions(int Limit = 50, string? SessionId = null);

/// <summary>
/// A host-local, per-host full-text (and later, embeddings) index over every session's transcript. The
/// text-only implementation is built on SQLite FTS5, an in-family addition since the host already depends
/// on <c>Microsoft.Data.Sqlite</c> for the event store. Events are indexed as they are appended; a client
/// (or, later, an agent) can then recall past work by term instead of scrolling one session at a time.
/// Registered as a plugin point so an embeddings-backed implementation can be added without changing core.
/// </summary>
public interface IMemoryIndexProvider
{
    /// <summary>Stable id used to select this backend (e.g. <c>text-only</c>, <c>embeddings</c>).</summary>
    string Id { get; }

    /// <summary>Indexes the searchable text of one appended event. A no-op for events with no text.</summary>
    Task IndexAsync(string sessionId, SessionEvent evt, CancellationToken cancellationToken = default);

    /// <summary>Searches the index, returning ranked results each carrying a highlighted snippet.</summary>
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, MemorySearchOptions options, CancellationToken cancellationToken = default);

    /// <summary>Indexes existing history within <paramref name="scope"/> — run off the hot path.</summary>
    Task BackfillAsync(MemoryBackfillScope scope, CancellationToken cancellationToken = default);

    /// <summary>Drops the on-disk index (delete-on-disable).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
