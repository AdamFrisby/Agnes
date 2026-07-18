using Agnes.Client;
using Agnes.Client.Simulation;

namespace Agnes.App.Desktop;

/// <summary>
/// Routes connections by URL scheme: <c>sim://</c> to the in-memory simulated server,
/// anything else (http/https) to the real SignalR host. Lets a single app talk to a mix of
/// simulated and real hosts, one per tab.
/// </summary>
public sealed class RoutingConnector : IAgnesConnector
{
    private readonly SimulatedConnector _simulated = new();
    private readonly SignalRConnector _real = new();

    public IReadOnlyCollection<IAgnesHost> Hosts => [.. _simulated.Hosts, .. _real.Hosts];

    public Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
        => hostUrl.StartsWith("sim:", StringComparison.OrdinalIgnoreCase)
            ? _simulated.ConnectAsync(hostUrl, token, cancellationToken)
            : _real.ConnectAsync(hostUrl, token, cancellationToken);
}
