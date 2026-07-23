using System.Collections.Concurrent;

namespace Agnes.Host.Sharing;

/// <summary>
/// Records which live hub connections are <em>public-link viewers</em> — connections that authenticated with a
/// public-link token rather than a device token — and the single session each is scoped to. A public viewer is
/// strictly read-only: the hub consults this tracker to allow such a connection to subscribe only to its one
/// session, and to reject every write path (prompt, permission approval, manage) outright. There is deliberately
/// no per-viewer access level here — the only state a public viewer can hold is "may read this one session".
/// </summary>
public sealed class PublicViewerTracker
{
    private readonly ConcurrentDictionary<string, string> _sessionByConnection = new(StringComparer.Ordinal);

    /// <summary>Marks a connection as a public viewer of exactly one session.</summary>
    public void Mark(string connectionId, string sessionId) => _sessionByConnection[connectionId] = sessionId;

    /// <summary>Forgets a connection (on disconnect).</summary>
    public void Remove(string connectionId) => _sessionByConnection.TryRemove(connectionId, out _);

    /// <summary>Whether this connection is a public-link viewer at all (i.e. has no device identity).</summary>
    public bool IsPublicViewer(string connectionId) => _sessionByConnection.ContainsKey(connectionId);

    /// <summary>Whether this connection is a public viewer permitted to read exactly <paramref name="sessionId"/>.</summary>
    public bool CanView(string connectionId, string sessionId)
        => _sessionByConnection.TryGetValue(connectionId, out var s) && string.Equals(s, sessionId, StringComparison.Ordinal);
}
