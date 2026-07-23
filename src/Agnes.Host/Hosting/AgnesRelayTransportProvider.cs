using System.Net.Sockets;
using Agnes.Relay.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Configuration for <see cref="AgnesRelayTransportProvider"/> (bound from <c>Agnes:Transport:Relay</c>).</summary>
public sealed record RelayTransportOptions
{
    /// <summary>The relay's address as <c>host:port</c> (an <c>agnes-relay://</c> or <c>tcp://</c> scheme is
    /// tolerated and stripped). This is where the host dials <b>out</b> — no inbound port is opened.</summary>
    public string Url { get; init; } = "";

    /// <summary>The stable routable id this host claims on the relay; clients address the host by it.</summary>
    public string HostId { get; init; } = "";

    /// <summary>Overrides the local Kestrel HTTPS port the blind pump forwards to. When null it is derived
    /// from the host's bound address(es).</summary>
    public int? HubPort { get; init; }
}

/// <summary>
/// Opens (and each spliced data connection) a fresh outbound connection to the relay. Injected so the
/// provider is testable against an in-process relay + loopback listener with no external network.
/// </summary>
public interface IRelayDialer
{
    /// <summary>Opens a new outbound connection to the relay (used for the control connection and every data connection).</summary>
    Task<Stream> ConnectRelayAsync(CancellationToken ct);

    /// <summary>Opens a loopback connection to the host's own local Kestrel HTTPS port (the blind pump target).</summary>
    Task<Stream> ConnectLocalAsync(int port, CancellationToken ct);
}

/// <summary>Default <see cref="IRelayDialer"/>: plain outbound TCP to the relay, and loopback TCP to Kestrel.</summary>
public sealed class TcpRelayDialer : IRelayDialer
{
    private readonly string _relayHost;
    private readonly int _relayPort;

    public TcpRelayDialer(string relayHost, int relayPort)
    {
        _relayHost = relayHost;
        _relayPort = relayPort;
    }

    public async Task<Stream> ConnectRelayAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_relayHost, _relayPort, ct).ConfigureAwait(false);
            tcp.NoDelay = true;
            return new TcpOwningStream(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task<Stream> ConnectLocalAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, ct).ConfigureAwait(false);
            tcp.NoDelay = true;
            return new TcpOwningStream(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>A <see cref="NetworkStream"/> that also disposes the owning <see cref="TcpClient"/>.</summary>
    private sealed class TcpOwningStream : Stream
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _inner;

        public TcpOwningStream(TcpClient tcp)
        {
            _tcp = tcp;
            _inner = tcp.GetStream();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _tcp.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Makes a host behind NAT reachable via the blind <c>Agnes.Relay</c> broker (spec AC2). The host dials the
/// relay <b>out</b> (<see cref="RequiresOutboundOnly"/> is true), authenticates with its per-host key, and
/// claims a routable host-id; when a client arrives the relay signals the host, which opens a fresh outbound
/// data connection and <b>blind-pumps</b> it to its own local Kestrel HTTPS listener over loopback.
/// <para>
/// TLS terminates at Kestrel with the certificate from <see cref="IHostCertificateProvider"/>, so the relay
/// <i>and</i> this loopback pump are pure byte-movers — the client↔host TLS is end-to-end. The advertised
/// client address encodes the relay + this host-id + the cert fingerprint to pin, and is never a LAN address
/// (AC5). If the relay is unreachable/misconfigured/unauthorized, <see cref="ExposeAsync"/> throws a clear,
/// actionable error with no silent fallback (AC6).
/// </para>
/// A later optimization could replace the loopback pump with a custom Kestrel connection-transport that feeds
/// the tunneled stream straight into the server pipeline; the loopback pump reuses the existing listener and is
/// far simpler, so it is what ships here.
/// </summary>
public sealed class AgnesRelayTransportProvider : ITransportProvider, IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly RelayTransportOptions _options;
    private readonly IRelayHostKey _key;
    private readonly IHostCertificateProvider _certificate;
    private readonly IRelayDialer _dialer;
    private readonly ILogger<AgnesRelayTransportProvider>? _logger;
    private readonly string _relayHost;
    private readonly int _relayPort;

    private CancellationTokenSource? _cts;
    private Stream? _control;
    private Task? _controlLoop;
    private TransportEndpoint? _endpoint;

    public AgnesRelayTransportProvider(
        RelayTransportOptions options,
        IRelayHostKey key,
        IHostCertificateProvider certificate,
        IRelayDialer? dialer = null,
        ILogger<AgnesRelayTransportProvider>? logger = null)
    {
        _options = options;
        _key = key;
        _certificate = certificate;
        _logger = logger;
        (_relayHost, _relayPort) = ParseRelay(options.Url);
        _dialer = dialer ?? new TcpRelayDialer(_relayHost, _relayPort);
    }

    /// <inheritdoc />
    public string Id => "agnes-relay";

    /// <inheritdoc />
    public string DisplayName => "Agnes Relay";

    /// <inheritdoc />
    public bool RequiresOutboundOnly => true;

    /// <inheritdoc />
    public TransportEndpoint Describe(HostExposureContext context)
        // Before ExposeAsync the relay hasn't accepted us; advertise nothing rather than a LAN address that
        // would be unreachable from off-LAN (AC5). Program's startup calls ExposeAsync, not this.
        => _endpoint ?? new TransportEndpoint([], "Agnes relay transport is not exposed yet — call ExposeAsync.");

    /// <inheritdoc />
    public async Task<TransportEndpoint> ExposeAsync(HostExposureContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.HostId))
        {
            throw new InvalidOperationException(
                "The Agnes relay transport requires a stable host-id. Set Agnes:Transport:Relay:HostId.");
        }

        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            throw new InvalidOperationException(
                "The Agnes relay transport requires the relay address. Set Agnes:Transport:Relay:Url (host:port).");
        }

        int hubPort = _options.HubPort ?? DeriveHubPort(context.BoundAddresses)
            ?? throw new InvalidOperationException(
                "The Agnes relay transport could not determine the local Kestrel HTTPS port to forward to. " +
                "Set Agnes:Transport:Relay:HubPort.");

        // Ensure the host cert exists before advertising it (self-signed: lazy no-op; ACME: runs the DNS-01
        // order now so a cert failure surfaces loudly at startup rather than on the first client, per AC6).
        await _certificate.EnsureReadyAsync(ct).ConfigureAwait(false);

        Stream control;
        try
        {
            control = await _dialer.ConnectRelayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // AC6: relay unreachable — fail loudly, never silently fall back to Direct.
            throw new InvalidOperationException(
                $"Could not reach the Agnes relay at '{_relayHost}:{_relayPort}'. Verify Agnes:Transport:Relay:Url " +
                "and that the relay is running and reachable from this host.", ex);
        }

        try
        {
            await RegisterAsync(control, ct).ConfigureAwait(false);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _control = control;
        _cts = new CancellationTokenSource();
        _controlLoop = Task.Run(() => ControlLoopAsync(control, hubPort, _cts.Token), CancellationToken.None);

        // AC5: advertise the RELAY address + host-id + how to trust the host cert — never a LAN address. A real-CA
        // cert advertises its CA-validated hostname (?cn=) so the client validates the chain+name; a self-signed
        // cert advertises its fingerprint (?fp=) for the client to pin.
        string? caName = _certificate.CaValidatedHostName;
        var (query, trustHint) = caName is not null
            ? ($"cn={Uri.EscapeDataString(caName)}", $"CA-validated host name {caName}")
            : ($"fp={_certificate.Fingerprint}", $"pinned host cert {_certificate.Fingerprint}");
        var address = $"agnes-relay://{_relayHost}:{_relayPort}/{_options.HostId}?{query}";
        _endpoint = new TransportEndpoint([address],
            $"Reachable via the Agnes relay at {_relayHost}:{_relayPort} ({trustHint}).");
        _logger?.LogInformation(
            "Registered host-id '{HostId}' on the Agnes relay at {Relay}; clients reach it via {Address} ({Trust}).",
            _options.HostId, $"{_relayHost}:{_relayPort}", address, trustHint);
        return _endpoint;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
            _control = null;
        }

        if (_controlLoop is not null)
        {
            try
            {
                await _controlLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Teardown of the control loop unwinding is expected.
            }

            _controlLoop = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RegisterAsync(Stream control, CancellationToken ct)
    {
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(HandshakeTimeout);
        CancellationToken token = handshakeCts.Token;

        await RelayFrameCodec.WriteFrameAsync(control, new HostHelloFrame(ProtocolVersion), token).ConfigureAwait(false);

        RelayFrame? challengeFrame = await RelayFrameCodec.ReadFrameAsync(control, token).ConfigureAwait(false);
        if (challengeFrame is not ChallengeFrame challenge)
        {
            throw new InvalidOperationException(
                "The Agnes relay did not issue a registration challenge (unexpected first frame). " +
                "Verify Agnes:Transport:Relay:Url points at an Agnes relay.");
        }

        string signature = _key.SignChallenge(challenge.Nonce, _options.HostId);
        await RelayFrameCodec.WriteFrameAsync(control,
            new HostRegisterFrame(_options.HostId, _key.PublicKeyBase64, signature), token).ConfigureAwait(false);

        RelayFrame? ackFrame = await RelayFrameCodec.ReadFrameAsync(control, token).ConfigureAwait(false);
        if (ackFrame is not RegisterAckFrame ack)
        {
            throw new InvalidOperationException("The Agnes relay did not acknowledge registration.");
        }

        if (!ack.Ok)
        {
            // AC6: unauthorized / misconfigured host key — a clear, actionable error, no fallback.
            throw new InvalidOperationException(
                $"The Agnes relay rejected this host's registration ({ack.Reason ?? "no reason given"}). " +
                "Add this host's relay public key to the relay's authorized-hosts file. Public key: " +
                _key.PublicKeyBase64);
        }
    }

    private async Task ControlLoopAsync(Stream control, int hubPort, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                RelayFrame? frame = await RelayFrameCodec.ReadFrameAsync(control, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (frame is ClientWaitingFrame waiting)
                {
                    // A client is waiting: open a fresh outbound data connection and pump it to loopback Kestrel.
                    _ = Task.Run(() => ServeDataConnectionAsync(waiting.Token, hubPort, ct), CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The control connection closing (relay restart / shutdown) is expected; StopAsync/teardown handles it.
            _logger?.LogDebug(ex, "Agnes relay control connection ended.");
        }
    }

    private async Task ServeDataConnectionAsync(string token, int hubPort, CancellationToken ct)
    {
        Stream? data = null;
        Stream? local = null;
        try
        {
            data = await _dialer.ConnectRelayAsync(ct).ConfigureAwait(false);
            await RelayFrameCodec.WriteFrameAsync(data, new HostDataFrame(token), ct).ConfigureAwait(false);

            // From here the data connection carries opaque client↔host TLS bytes. Splice it to the local
            // Kestrel HTTPS port and copy both directions verbatim — TLS terminates at Kestrel, so this pump
            // is blind by construction (see BlindPumpAsync).
            local = await _dialer.ConnectLocalAsync(hubPort, ct).ConfigureAwait(false);
            await BlindPumpAsync(data, local, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _logger?.LogDebug(ex, "Agnes relay data connection ended.");
        }
        finally
        {
            if (data is not null)
            {
                await data.DisposeAsync().ConfigureAwait(false);
            }

            if (local is not null)
            {
                await local.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Copies bytes both directions until either side closes — the ONLY thing that touches payload bytes, and
    /// it merely moves them. No TLS parsing, no buffering-with-inspection, no logging of content: the blindness
    /// invariant, by construction (mirrors the relay's own splice).
    /// </summary>
    private static async Task BlindPumpAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Pump(Stream from, Stream to)
        {
            try
            {
                await from.CopyToAsync(to, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                linked.Cancel(); // one direction ended → unblock and close the other.
            }
        }

        try
        {
            await Task.WhenAll(Pump(a, b), Pump(b, a)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Expected when one side closes and we cancel the other mid-copy.
        }
    }

    private static (string Host, int Port) ParseRelay(string url)
    {
        string value = (url ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return ("", 0);
        }

        // Tolerate a scheme prefix (agnes-relay://host:port, tcp://host:port) as well as a bare host:port.
        int schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            value = value[(schemeIndex + 3)..];
        }

        value = value.TrimEnd('/');
        int colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1
            || !int.TryParse(value[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out int port))
        {
            throw new InvalidOperationException(
                $"Agnes:Transport:Relay:Url must be 'host:port' (got '{url}').");
        }

        return (value[..colon], port);
    }

    private static int? DeriveHubPort(IReadOnlyList<string> boundAddresses)
    {
        // Prefer an https endpoint (TLS terminates at Kestrel on the relay path); fall back to any bound port.
        foreach (bool wantHttps in (bool[])[true, false])
        {
            foreach (string address in boundAddresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Port > 0
                    && (!wantHttps || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
                {
                    return uri.Port;
                }
            }
        }

        return null;
    }
}
