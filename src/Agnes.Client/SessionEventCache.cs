using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// The stretch of one session's log a client holds locally: every event the host returned for a
/// <c>Subscribe(since &gt;= Floor)</c>, up to and including <see cref="Head"/>. The head's kind and
/// timestamp are the fingerprint a later replay checks against the host before trusting the rest.
/// </summary>
/// <param name="Floor">Exclusive lower bound: a request with <c>since &gt;= Floor</c> can be answered from
/// the cache up to <see cref="Head"/>. Kept as the bound rather than the first cached sequence because
/// sequences need not be dense.</param>
public sealed record CachedRange(long Floor, long Head, string HeadKind, DateTimeOffset HeadTimestamp);

/// <summary>
/// A local, durable copy of session events, keyed by host, session and sequence.
/// </summary>
/// <remarks>
/// The event log is append-only and a sequence, once assigned, never changes meaning, which is what makes a
/// client-side cache safe: what was true of sequence 4,000 yesterday is true today. A session with a few
/// hundred thousand events is a hundred-plus megabytes over the wire on every open without one; with one,
/// the second open reads the log from disk and fetches only what arrived since. One contiguous range is
/// kept per session (see <see cref="WriteAsync"/> for how ranges merge), and an implementation is expected
/// to be cheap on the write path — live events are recorded one at a time as they are applied.
/// </remarks>
public interface ISessionEventCache
{
    /// <summary>The range held for a session, or null when nothing is.</summary>
    Task<CachedRange?> RangeAsync(string hostId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Cached events with <c>Sequence &gt; since</c>, ascending. Meaningful only when
    /// <paramref name="since"/> is at or above the range's floor.</summary>
    Task<IReadOnlyList<SessionEvent>> ReadAsync(string hostId, string sessionId, long since, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the host's answer to <c>Subscribe(since)</c>: <paramref name="events"/> are everything the
    /// host holds in <c>(since, max sequence]</c>. A batch that overlaps or touches the held range extends
    /// it; a disjoint one replaces it only if it spans more, so a stray single event can never evict a
    /// long history.
    /// </summary>
    Task WriteAsync(string hostId, string sessionId, long since, IReadOnlyList<SessionEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Drops everything held for a session — the response to a host whose log no longer matches.</summary>
    Task ForgetAsync(string hostId, string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>What a replay served from where; raised for diagnostics and the odd status line.</summary>
public sealed record ReplayReport(string SessionId, long Since, int FromCache, int FromHost, bool Invalidated);

/// <summary>
/// The replay policy over an <see cref="ISessionEventCache"/>: answers a subscribe from the cache where it
/// can, asks the host only for what it lacks, and verifies the join before trusting the cached part.
/// </summary>
/// <remarks>
/// <para>The verification is one event wide. The host is asked for events after <c>Head − 1</c>, so the
/// first thing it returns must be the cached head itself — same sequence, same kind, same timestamp. A
/// host whose log was reset, re-imported or otherwise rewritten fails that check, or reports a head below
/// the cached one, and the cache for that session is dropped and the request made afresh. Nothing about
/// the cache is trusted across that boundary.</para>
/// <para>The cache is never allowed to break a subscribe: any failure reading or writing it falls back to
/// the plain host request.</para>
/// </remarks>
public sealed class CachedReplay(ISessionEventCache cache, string hostId)
{
    /// <summary>The cache's name for an event's kind: the CLR type, which the JSON discriminator also encodes.</summary>
    public static string KindOf(SessionEvent @event) => @event.GetType().Name;

    /// <summary>Raised after each replay with where its events came from.</summary>
    public event Action<ReplayReport>? Replayed;

    public async Task<SessionSnapshot> FetchAsync(
        string sessionId, long since, Func<long, Task<SessionSnapshot>> fetch, CancellationToken cancellationToken = default)
    {
        CachedRange? range;
        try
        {
            range = await cache.RangeAsync(hostId, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await fetch(since).ConfigureAwait(false);
        }

        if (range is null || since < range.Floor || since >= range.Head)
        {
            return await DirectAsync(sessionId, since, fetch, invalidated: false, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<SessionEvent> local;
        try
        {
            local = await cache.ReadAsync(hostId, sessionId, since, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await fetch(since).ConfigureAwait(false);
        }

        var remote = await fetch(range.Head - 1).ConfigureAwait(false);
        var probe = remote.Events.Count > 0 ? remote.Events[0] : null;
        var consistent = remote.HeadSequence >= range.Head
            && probe is not null
            && probe.Sequence == range.Head
            && string.Equals(KindOf(probe), range.HeadKind, StringComparison.Ordinal)
            && probe.Timestamp == range.HeadTimestamp
            && local.Count > 0
            && local[^1].Sequence == range.Head;
        if (!consistent)
        {
            await Quietly(() => cache.ForgetAsync(hostId, sessionId, cancellationToken)).ConfigureAwait(false);
            return await DirectAsync(sessionId, since, fetch, invalidated: true, cancellationToken).ConfigureAwait(false);
        }

        var tail = remote.Events.Skip(1).ToList();
        await Quietly(() => cache.WriteAsync(hostId, sessionId, range.Head, tail, cancellationToken)).ConfigureAwait(false);

        var merged = new List<SessionEvent>(local.Count + tail.Count);
        merged.AddRange(local);
        merged.AddRange(tail);
        Replayed?.Invoke(new ReplayReport(sessionId, since, local.Count, tail.Count, Invalidated: false));
        return remote with { Events = merged };
    }

    /// <summary>Records a live event the view just applied, whose predecessor in the view was
    /// <paramref name="previousLast"/>. Fire-and-forget: the hub callback that delivers events must not wait
    /// on disk.</summary>
    public void RecordLive(string sessionId, long previousLast, SessionEvent @event)
        => _ = Quietly(() => cache.WriteAsync(hostId, sessionId, previousLast, [@event]));

    private async Task<SessionSnapshot> DirectAsync(
        string sessionId, long since, Func<long, Task<SessionSnapshot>> fetch, bool invalidated, CancellationToken cancellationToken)
    {
        var snapshot = await fetch(since).ConfigureAwait(false);
        await Quietly(() => cache.WriteAsync(hostId, sessionId, since, snapshot.Events, cancellationToken)).ConfigureAwait(false);
        Replayed?.Invoke(new ReplayReport(sessionId, since, 0, snapshot.Events.Count, invalidated));
        return snapshot;
    }

    private static async Task Quietly(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cache that cannot be written is a cache that is not there; the session must still open.
        }
    }
}
