using System.Collections.Concurrent;
using Agnes.Client;

namespace Agnes.Client.Simulation;

/// <summary>A connector that hands out in-memory <see cref="SimulatedHost"/>s instead of SignalR.</summary>
public sealed class SimulatedConnector : IAgnesConnector
{
    private readonly ConcurrentDictionary<string, SimulatedHost> _hosts = new();

    public IReadOnlyCollection<IAgnesHost> Hosts => _hosts.Values.Cast<IAgnesHost>().ToArray();

    public async Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
    {
        var host = _hosts.GetOrAdd(hostUrl, url => new SimulatedHost(url));
        if (host.State != AgnesConnectionState.Connected)
        {
            await host.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        return host;
    }

    public async Task RemoveAsync(string hostUrl)
    {
        if (_hosts.TryRemove(hostUrl, out var host))
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
