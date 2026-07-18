using Agnes.Client;
using Agnes.Client.Simulation;

namespace Agnes.App.Desktop;

/// <summary>
/// Routes connections by URL scheme: <c>sim://</c> to the in-memory simulated server,
/// <c>rec://</c> to recorded-session playback (real captured test data), and anything else
/// (http/https) to the real SignalR host. Lets a single window mix hosts, one per tab.
/// </summary>
public sealed class RoutingConnector : IAgnesConnector
{
    private readonly SimulatedConnector _simulated = new();
    private readonly SignalRConnector _real = new();
    private readonly RecordedConnector _recorded;

    public RoutingConnector(string recordingsDirectory, double recordingSpeed = 1.0)
        => _recorded = new RecordedConnector(recordingsDirectory, recordingSpeed);

    public IReadOnlyCollection<IAgnesHost> Hosts => [.. _simulated.Hosts, .. _recorded.Hosts, .. _real.Hosts];

    public Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
    {
        if (hostUrl.StartsWith("sim:", StringComparison.OrdinalIgnoreCase))
        {
            return _simulated.ConnectAsync(hostUrl, token, cancellationToken);
        }

        if (hostUrl.StartsWith("rec:", StringComparison.OrdinalIgnoreCase))
        {
            return _recorded.ConnectAsync(hostUrl, token, cancellationToken);
        }

        return _real.ConnectAsync(hostUrl, token, cancellationToken);
    }
}
