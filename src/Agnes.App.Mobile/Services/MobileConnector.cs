using Agnes.Client;
using Agnes.Client.Simulation;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// Routes a connection by URL scheme: <c>sim://</c> to the in-memory demo host, anything else to the
/// real SignalR host.
///
/// The demo is not a debug affordance — it ships. A remote-agent client is useless until you have a
/// host, and "install it, then go set up a server before you can see anything" is a bad first minute.
/// The simulated host streams a real scripted session through the same event pipeline as a live one,
/// so what you see is genuinely how the app behaves.
/// </summary>
public sealed class MobileConnector : IAgnesConnector
{
    private readonly SimulatedConnector _demo = new();
    private readonly SignalRConnector _real = new();

    public IReadOnlyCollection<IAgnesHost> Hosts => [.. _demo.Hosts, .. _real.Hosts];

    public Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
        => DemoHost.IsDemo(hostUrl)
            ? _demo.ConnectAsync(hostUrl, token, cancellationToken)
            : _real.ConnectAsync(hostUrl, token, cancellationToken);

    public Task<IAgnesHost> ConnectAsync(
        string hostUrl, string token, string? pinnedFingerprint, CancellationToken cancellationToken = default)
        => DemoHost.IsDemo(hostUrl)
            ? _demo.ConnectAsync(hostUrl, token, cancellationToken)
            : _real.ConnectAsync(hostUrl, token, pinnedFingerprint, cancellationToken);

    public Task RemoveAsync(string hostUrl)
        => DemoHost.IsDemo(hostUrl) ? _demo.RemoveAsync(hostUrl) : _real.RemoveAsync(hostUrl);
}
