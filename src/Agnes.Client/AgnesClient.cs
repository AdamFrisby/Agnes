using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Agnes.Client;

/// <summary>
/// Entry point for a frontend: a pool of <see cref="HostConnection"/>s so one client can
/// talk to dozens of agents across multiple hosts. Add a host, then drive it via its
/// <see cref="HostConnection"/>.
/// </summary>
public sealed class AgnesClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HostConnection> _hosts = new();

    /// <summary>All currently connected hosts, keyed by URL.</summary>
    public IReadOnlyCollection<HostConnection> Hosts => _hosts.Values.ToArray();

    /// <summary>
    /// A per-host status snapshot across the pool — the host-list surface. Each entry carries the host's
    /// identity, the transport reaching it (which can differ host to host — one Direct-LAN, one via relay, one
    /// via Tailscale), its independent connection state, and its session count.
    /// </summary>
    public IReadOnlyList<HostStatus> HostStatuses =>
        _hosts.Values
            .Select(h => new HostStatus(h.HostId, h.HostUrl, h.Transport, h.State, h.Sessions.Count))
            .ToArray();

    /// <summary>
    /// The union of sessions across every pooled host, each tagged with the host it lives on. Because a session
    /// id is unique only within a host, the tag is what keeps a same-named session on two different hosts
    /// distinct in the aggregate.
    /// </summary>
    public IReadOnlyList<HostSessionRef> AllSessions =>
        _hosts.Values
            .SelectMany(h => h.Sessions.Select(s => new HostSessionRef(h.HostId, h.HostUrl, h.Transport, s)))
            .ToArray();

    /// <summary>Looks up a pooled host by the URL it was added with, or null if it isn't in the pool.</summary>
    public HostConnection? FindHost(string hostUrl)
        => _hosts.TryGetValue(hostUrl.TrimEnd('/'), out var host) ? host : null;

    /// <summary>Adds and connects a host. Returns the existing connection if already added.</summary>
    public async Task<HostConnection> AddHostAsync(
        string hostUrl,
        string token,
        Action<HttpConnectionOptions>? configureHttp = null,
        CancellationToken cancellationToken = default)
    {
        var key = hostUrl.TrimEnd('/');
        if (_hosts.TryGetValue(key, out var existing))
        {
            // Reuse a live connection, but never hand back a dead one: if the host restarted (or the
            // connection dropped and auto-reconnect gave up), the pooled hub is Disconnected/Reconnecting
            // and any call throws "the connection is not active". Drop it and reconnect fresh with the
            // current token instead.
            if (existing.State == AgnesConnectionState.Connected)
            {
                return existing;
            }

            _hosts.TryRemove(key, out _);
            await existing.DisposeAsync();
        }

        var connection = new HostConnection(hostUrl, token, configureHttp);
        if (!_hosts.TryAdd(key, connection))
        {
            await connection.DisposeAsync();
            return _hosts[key];
        }

        await connection.ConnectAsync(cancellationToken);
        return connection;
    }

    public async Task RemoveHostAsync(string hostUrl)
    {
        if (_hosts.TryRemove(hostUrl.TrimEnd('/'), out var connection))
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Reconnects a single pooled host in place, independently of the others. Auto-reconnect already covers
    /// transient drops; this is the explicit path for when it has given up (or a host is re-added). One host's
    /// reconnect never disturbs another's connection — each <see cref="HostConnection"/> owns its own hub.
    /// Returns the (re)connected host, or null if <paramref name="hostUrl"/> isn't in the pool.
    /// </summary>
    public async Task<HostConnection?> ReconnectHostAsync(string hostUrl, CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryGetValue(hostUrl.TrimEnd('/'), out var connection))
        {
            return null;
        }

        if (connection.State != AgnesConnectionState.Connected)
        {
            await connection.ConnectAsync(cancellationToken);
        }

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _hosts.Values)
        {
            await connection.DisposeAsync();
        }

        _hosts.Clear();
    }
}
