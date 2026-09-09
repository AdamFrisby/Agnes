using Agnes.Client;
#if DEBUG
using Agnes.Client.Simulation;
#endif

namespace Agnes.App.Mobile.Services;

/// <summary>
/// Routes a connection by URL scheme: <c>sim://</c> to the in-memory demo host, anything else to the
/// real SignalR host.
///
/// The demo exists so the app has something true to show before you have a host to point it at — it
/// streams a scripted session through the same event pipeline as a live one. It is <b>Debug only</b>:
/// a shipped build must contain no simulated host and no scripted transcripts, so in Release the
/// simulation assembly isn't referenced and every <c>sim://</c> path below is compiled out.
/// </summary>
public sealed class MobileConnector : IAgnesConnector
{
#if DEBUG
    private readonly SimulatedConnector _demo = new();
#endif
    private readonly SignalRConnector _real = new();

#if DEBUG
    public IReadOnlyCollection<IAgnesHost> Hosts => [.. _demo.Hosts, .. _real.Hosts];
#else
    public IReadOnlyCollection<IAgnesHost> Hosts => [.. _real.Hosts];
#endif

    public Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
#if DEBUG
        => DemoHost.IsDemo(hostUrl)
            ? _demo.ConnectAsync(hostUrl, token, cancellationToken)
            : _real.ConnectAsync(hostUrl, token, cancellationToken);
#else
        => _real.ConnectAsync(hostUrl, token, cancellationToken);
#endif

    public Task<IAgnesHost> ConnectAsync(
        string hostUrl, string token, string? pinnedFingerprint, CancellationToken cancellationToken = default)
#if DEBUG
        => DemoHost.IsDemo(hostUrl)
            ? _demo.ConnectAsync(hostUrl, token, cancellationToken)
            : _real.ConnectAsync(hostUrl, token, pinnedFingerprint, cancellationToken);
#else
        => _real.ConnectAsync(hostUrl, token, pinnedFingerprint, cancellationToken);
#endif

    public Task RemoveAsync(string hostUrl)
#if DEBUG
        => DemoHost.IsDemo(hostUrl) ? _demo.RemoveAsync(hostUrl) : _real.RemoveAsync(hostUrl);
#else
        => _real.RemoveAsync(hostUrl);
#endif
}
