using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Events;

/// <summary>
/// An <see cref="IEventStore"/> decorator that feeds every appended event into an
/// <see cref="IMemoryIndexProvider"/> for full-text recall. It observes the single append boundary
/// instead of scattering index calls across every call site, and keeps the concern out of the concrete
/// stores. Indexing is best-effort: a failure is logged and swallowed so it can never break — or even
/// slow to the point of failing — the durable append it rides behind.
/// </summary>
public sealed class IndexingEventStore : IEventStore
{
    private readonly IEventStore _inner;
    private readonly IMemoryIndexProvider _index;
    private readonly ILogger<IndexingEventStore> _logger;

    public IndexingEventStore(IEventStore inner, IMemoryIndexProvider index, ILogger<IndexingEventStore> logger)
    {
        _inner = inner;
        _index = index;
        _logger = logger;
    }

    public async Task<SessionEvent> AppendAsync(string sessionId, SessionEvent @event, CancellationToken cancellationToken = default)
    {
        var stored = await _inner.AppendAsync(sessionId, @event, cancellationToken).ConfigureAwait(false);
        try
        {
            await _index.IndexAsync(sessionId, stored, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let an indexing hiccup surface as an append failure — the durable log is authoritative
            // and backfill can rebuild any missed rows later.
            _logger.LogWarning(ex, "Failed to index event {Sequence} for session {SessionId}", stored.Sequence, sessionId);
        }

        return stored;
    }

    public Task<IReadOnlyList<SessionEvent>> ReadSinceAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default)
        => _inner.ReadSinceAsync(sessionId, sinceSequence, cancellationToken);

    public Task<long> GetHeadAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.GetHeadAsync(sessionId, cancellationToken);

    public Task SaveSessionAsync(SessionRecord record, CancellationToken cancellationToken = default)
        => _inner.SaveSessionAsync(record, cancellationToken);

    public Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => _inner.ListSessionsAsync(cancellationToken);
}
