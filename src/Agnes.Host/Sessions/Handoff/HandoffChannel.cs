using System.Net.Sockets;

namespace Agnes.Host.Sessions.Handoff;

/// <summary>A host a handoff can target: its stable id and the address it advertises for connections
/// (a direct <c>host:port</c>/URL, or an <c>agnes-relay://…</c> address for relay-only hosts).</summary>
public sealed record HostEndpoint(string HostId, string Address);

/// <summary>Which route a <see cref="IHandoffChannel"/> took between the two hosts.</summary>
public enum HandoffChannelKind
{
    /// <summary>An authenticated point-to-point connection — the two hosts reach each other directly.</summary>
    Direct,

    /// <summary>Blind byte-forwarding through the relay (connectivity/01) when a direct route isn't available.
    /// The relay never understands the payload.</summary>
    Relay,
}

/// <summary>
/// An authenticated byte pipe between two hosts, carrying the workspace-transfer stream (and reusable for any
/// host-to-host bytes). Modelled as a <see cref="Stream"/> exactly like the relay's own splice
/// (connectivity/01), so both the direct and relay routes present the same surface — we never stand up a
/// second transport for file transfer.
/// </summary>
public interface IHandoffChannel : IAsyncDisposable
{
    /// <summary>Which route this channel took (chosen by <see cref="HandoffChannelSelector"/>).</summary>
    HandoffChannelKind Kind { get; }

    /// <summary>The opaque byte pipe to the other host.</summary>
    Stream Transport { get; }
}

/// <summary>A directly-dialed host-to-host channel (both hosts reach each other, e.g. same LAN/overlay).</summary>
public sealed class DirectHandoffChannel(Stream transport) : IHandoffChannel
{
    public HandoffChannelKind Kind => HandoffChannelKind.Direct;

    public Stream Transport { get; } = transport;

    public ValueTask DisposeAsync() => Transport.DisposeAsync();
}

/// <summary>A relay-routed host-to-host channel, riding the relay's blind byte-forwarding (connectivity/01)
/// when the target isn't directly reachable.</summary>
public sealed class RelayHandoffChannel(Stream transport) : IHandoffChannel
{
    public HandoffChannelKind Kind => HandoffChannelKind.Relay;

    public Stream Transport { get; } = transport;

    public ValueTask DisposeAsync() => Transport.DisposeAsync();
}

/// <summary>Tests whether a target host is <em>directly</em> reachable from this host (its advertised address
/// dials successfully). The result drives direct-vs-relay routing in <see cref="HandoffChannelSelector"/>.</summary>
public interface IHostReachabilityProbe
{
    Task<bool> IsDirectlyReachableAsync(HostEndpoint target, CancellationToken ct = default);
}

/// <summary>Opens the two concrete host-to-host routes. Split from the selector so the routing decision
/// (which one) is testable in isolation from the transport (how each is dialled).</summary>
public interface IHandoffChannelFactory
{
    Task<IHandoffChannel> OpenDirectAsync(HostEndpoint target, CancellationToken ct = default);

    Task<IHandoffChannel> OpenRelayAsync(HostEndpoint target, CancellationToken ct = default);
}

/// <summary>
/// The maintainer's routing decision, made testable: probe whether the target is directly reachable and, if
/// so, take the <see cref="HandoffChannelKind.Direct"/> route; otherwise fall back to the
/// <see cref="HandoffChannelKind.Relay"/> route. Pure over its injected <see cref="IHostReachabilityProbe"/>
/// and <see cref="IHandoffChannelFactory"/>, so the selection can be asserted with a stubbed probe.
/// </summary>
public sealed class HandoffChannelSelector(IHostReachabilityProbe probe, IHandoffChannelFactory factory)
{
    public async Task<IHandoffChannel> OpenAsync(HostEndpoint target, CancellationToken ct = default)
        => await probe.IsDirectlyReachableAsync(target, ct).ConfigureAwait(false)
            ? await factory.OpenDirectAsync(target, ct).ConfigureAwait(false)
            : await factory.OpenRelayAsync(target, ct).ConfigureAwait(false);
}

/// <summary>
/// Default reachability probe: a host is "directly reachable" when its advertised <c>host:port</c> accepts a
/// TCP connection within <see cref="_timeout"/>. Relay-only addresses (<c>agnes-relay://…</c>) advertise no
/// direct route and are reported unreachable, so those hosts fall back to the relay. The <see cref="TimeProvider"/>
/// is injected so the timeout is deterministic under test (production sessions pass <see cref="TimeProvider.System"/>).
/// </summary>
public sealed class TcpHostReachabilityProbe(TimeProvider clock, TimeSpan? timeout = null) : IHostReachabilityProbe
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);

    public async Task<bool> IsDirectlyReachableAsync(HostEndpoint target, CancellationToken ct = default)
    {
        if (!TryParseDirectEndpoint(target.Address, out var host, out var port))
        {
            return false;
        }

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var connect = socket.ConnectAsync(host, port, ct).AsTask();
        var timeout = Task.Delay(_timeout, clock, ct);
        var finished = await Task.WhenAny(connect, timeout).ConfigureAwait(false);
        if (finished != connect)
        {
            return false;
        }

        try
        {
            await connect.ConfigureAwait(false);
            return socket.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // A directly-dialable address is a bare host:port or an http(s)/agnes URL with an explicit authority —
    // NOT an agnes-relay:// address, which by construction has no direct route.
    private static bool TryParseDirectEndpoint(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(address) ||
            address.StartsWith("agnes-relay://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && uri.Port > 0)
        {
            host = uri.Host;
            port = uri.Port;
            return true;
        }

        // Bare "host:port".
        var colon = address.LastIndexOf(':');
        if (colon > 0 && colon < address.Length - 1 &&
            int.TryParse(address.AsSpan(colon + 1), System.Globalization.CultureInfo.InvariantCulture, out port) &&
            port is > 0 and <= 65535)
        {
            host = address[..colon];
            return true;
        }

        return false;
    }
}

/// <summary>
/// Default channel factory. Both routes present the same <see cref="Stream"/> surface: the relay route reuses
/// the existing relay byte-forwarding (connectivity/01) via the injected dialer, and the direct route dials the
/// target's advertised authority. Dialers are injected (not hard-wired) so production supplies the real relay
/// transport + a token-authenticated direct dial, while tests can supply in-memory pipes — no second transport
/// is introduced for handoff.
/// </summary>
public sealed class HandoffChannelFactory(
    Func<HostEndpoint, CancellationToken, Task<Stream>> dialDirect,
    Func<HostEndpoint, CancellationToken, Task<Stream>> dialRelay) : IHandoffChannelFactory
{
    public async Task<IHandoffChannel> OpenDirectAsync(HostEndpoint target, CancellationToken ct = default)
        => new DirectHandoffChannel(await dialDirect(target, ct).ConfigureAwait(false));

    public async Task<IHandoffChannel> OpenRelayAsync(HostEndpoint target, CancellationToken ct = default)
        => new RelayHandoffChannel(await dialRelay(target, ct).ConfigureAwait(false));
}
