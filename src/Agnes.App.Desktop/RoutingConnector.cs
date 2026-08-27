using Agnes.Client;
using Agnes.Client.Simulation;

namespace Agnes.App.Desktop;

/// <summary>
/// Routes connections by URL scheme: <c>sim://</c> to the in-memory simulated server,
/// <c>rec://</c> to recorded-session playback (real captured test data), and anything else
/// (http/https) to the real SignalR host. Lets a single window mix hosts, one per tab.
/// </summary>
/// <remarks>
/// <para>The simulated host is <b>Debug only</b>, matching the Android head (see
/// <c>MobileConnector</c>): it exists so the app has something honest to show before you have a host to
/// point it at, and a shipped build must not offer a fabricated one. A fake host in a released client's
/// list is worse than an empty list, because it looks like real data.</para>
///
/// <para>Recorded playback is <i>not</i> compiled out. Unlike the simulation it invents nothing — it
/// replays sessions the user actually captured, from a directory they control
/// (<c>AGNES_RECORDINGS</c>, else <c>%APPDATA%/Agnes/recordings</c>) — so it stays a real feature of a
/// shipped build. That is also why this file still references the simulation assembly in Release:
/// <see cref="RecordedConnector"/> lives there too.</para>
///
/// <para>A <c>sim://</c> URL can still reach a Release build from saved tab state written by a Debug
/// run, so the scheme is rejected explicitly rather than falling through to the SignalR connector,
/// which would fail on the URI and report it as an unreachable network host.</para>
/// </remarks>
public sealed class RoutingConnector : IAgnesConnector
{
#if DEBUG
    private readonly SimulatedConnector _simulated = new();
#endif
    private readonly SignalRConnector _real = new();
    private readonly RecordedConnector _recorded;

    public RoutingConnector(string recordingsDirectory, double recordingSpeed = 1.0)
        => _recorded = new RecordedConnector(recordingsDirectory, recordingSpeed);

    private static bool IsSimulated(string hostUrl) => hostUrl.StartsWith("sim:", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecorded(string hostUrl) => hostUrl.StartsWith("rec:", StringComparison.OrdinalIgnoreCase);

#if DEBUG
    public IReadOnlyCollection<IAgnesHost> Hosts => [.. _simulated.Hosts, .. _recorded.Hosts, .. _real.Hosts];
#else
    public IReadOnlyCollection<IAgnesHost> Hosts => [.. _recorded.Hosts, .. _real.Hosts];
#endif

    public Task<IAgnesHost> ConnectAsync(
        string hostUrl, string token, string? pinnedFingerprint, CancellationToken cancellationToken = default)
        => IsSimulated(hostUrl) || IsRecorded(hostUrl)
            ? ConnectAsync(hostUrl, token, cancellationToken)   // nothing to pin on an in-process host
            : _real.ConnectAsync(hostUrl, token, pinnedFingerprint, cancellationToken);

    public Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
    {
        if (IsSimulated(hostUrl))
        {
#if DEBUG
            return _simulated.ConnectAsync(hostUrl, token, cancellationToken);
#else
            return Task.FromException<IAgnesHost>(
                new NotSupportedException("The simulated host is available in development builds only."));
#endif
        }

        if (IsRecorded(hostUrl))
        {
            return _recorded.ConnectAsync(hostUrl, token, cancellationToken);
        }

        return _real.ConnectAsync(hostUrl, token, cancellationToken);
    }

    public Task RemoveAsync(string hostUrl)
    {
        if (IsSimulated(hostUrl))
        {
#if DEBUG
            return _simulated.RemoveAsync(hostUrl);
#else
            // Nothing to detach in a Release build, but the caller is dropping a stale saved entry and
            // must not be blocked from doing so.
            return Task.CompletedTask;
#endif
        }

        if (IsRecorded(hostUrl))
        {
            return _recorded.RemoveAsync(hostUrl);
        }

        return _real.RemoveAsync(hostUrl);
    }
}
