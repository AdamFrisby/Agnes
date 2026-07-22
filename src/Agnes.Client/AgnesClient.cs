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

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _hosts.Values)
        {
            await connection.DisposeAsync();
        }

        _hosts.Clear();
    }
}
