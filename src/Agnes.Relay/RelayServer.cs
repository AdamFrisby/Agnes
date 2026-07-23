using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Agnes.Relay.Protocol;

namespace Agnes.Relay;

/// <summary>
/// The relay: a blind byte broker. Hosts (behind NAT) open a persistent OUTBOUND control
/// connection and authenticate with their per-host key to claim a host-id. Clients dial the relay,
/// send a one-field routing header (the host-id), and the relay pairs them to the target host and
/// forwards opaque bytes both directions — it never parses TLS or <c>Agnes.Protocol</c>.
///
/// Pairing across NAT (the host can't accept inbound): when a client for host <c>H</c> arrives, the
/// relay signals <c>H</c>'s control connection ("client waiting, token T"); the host opens a NEW
/// outbound data connection presenting T; the relay splices that data connection to the waiting
/// client and blind-forwards each way. No custom multiplexer, no payload inspection.
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    private readonly RelayOptions _options;
    private readonly IAuthorizedHostKeys _authorizedKeys;
    private readonly RelayRateLimiter _rateLimiter;
    private readonly IRelayLog _log;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, HostRegistration> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingClient> _pending = new(StringComparer.Ordinal);
    private readonly object _claimGate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public RelayServer(
        RelayOptions options,
        IAuthorizedHostKeys authorizedKeys,
        RelayRateLimiter? rateLimiter = null,
        IRelayLog? log = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _authorizedKeys = authorizedKeys;
        _log = log ?? NullRelayLog.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _rateLimiter = rateLimiter ?? new RelayRateLimiter(options.RateLimit, _time);
    }

    /// <summary>The port actually bound (useful when <see cref="RelayOptions.Port"/> was 0 / ephemeral).</summary>
    public int Port => _listener is null
        ? throw new InvalidOperationException("Relay not started.")
        : ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("Relay already started.");
        }

        IPAddress address = IPAddress.Parse(_options.ListenAddress);
        _listener = new TcpListener(address, _options.Port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info($"Relay listening on {_listener.LocalEndpoint}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(tcp, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        string sourceIp = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        bool reserved = false;
        bool ownershipTransferred = false;
        try
        {
            if (!_rateLimiter.TryAcceptConnection(sourceIp))
            {
                _log.Warn($"Rejected connection from {sourceIp} (rate limit / ban)");
                return;
            }

            reserved = true;
            tcp.NoDelay = true;
            Stream stream = tcp.GetStream();

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(_options.HandshakeTimeout);
            RelayFrame? first = await RelayFrameCodec.ReadFrameAsync(stream, handshakeCts.Token).ConfigureAwait(false);

            switch (first)
            {
                case HostHelloFrame:
                    await HandleHostControlAsync(tcp, stream, sourceIp, ct).ConfigureAwait(false);
                    break;
                case ClientRouteFrame route:
                    ownershipTransferred = await HandleClientAsync(tcp, stream, sourceIp, route, ct).ConfigureAwait(false);
                    break;
                case HostDataFrame data:
                    ownershipTransferred = HandleHostData(tcp, stream, data);
                    break;
                default:
                    _log.Warn($"Dropping connection from {sourceIp}: unexpected first frame");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or EndOfStreamException)
        {
            _log.Warn($"Connection from {sourceIp} ended: {ex.GetType().Name}");
        }
        finally
        {
            if (reserved)
            {
                _rateLimiter.ReleaseConnection(sourceIp);
            }

            if (!ownershipTransferred)
            {
                tcp.Dispose();
            }
        }
    }

    // ---- Host control connection -------------------------------------------------------------

    private async Task HandleHostControlAsync(TcpClient tcp, Stream stream, string sourceIp, CancellationToken ct)
    {
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await RelayFrameCodec.WriteFrameAsync(stream, new ChallengeFrame(nonce), ct).ConfigureAwait(false);

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(_options.HandshakeTimeout);
        RelayFrame? frame = await RelayFrameCodec.ReadFrameAsync(stream, handshakeCts.Token).ConfigureAwait(false);
        if (frame is not HostRegisterFrame register)
        {
            await RelayFrameCodec.WriteFrameAsync(stream, new RegisterAckFrame(false, "expected host_register"), ct).ConfigureAwait(false);
            return;
        }

        byte[]? spki = HostKeyVerifier.Verify(_authorizedKeys, nonce, register.HostId, register.PublicKey, register.Signature);
        if (spki is null)
        {
            _log.Warn($"Host registration from {sourceIp} rejected: unauthorized key or bad signature for host-id '{register.HostId}'");
            await RelayFrameCodec.WriteFrameAsync(stream, new RegisterAckFrame(false, "unauthorized"), ct).ConfigureAwait(false);
            return;
        }

        var registration = new HostRegistration(register.HostId, spki, stream);
        bool claimed;
        lock (_claimGate)
        {
            // Refuse host-id squatting: an id already held by a DIFFERENT key can't be taken over. A
            // reconnect with the SAME key is allowed and replaces the previous registration.
            bool squatting = _hosts.TryGetValue(register.HostId, out HostRegistration? existing)
                && !existing.Spki.AsSpan().SequenceEqual(spki);
            claimed = !squatting;
            if (claimed)
            {
                _hosts[register.HostId] = registration;
            }
        }

        if (!claimed)
        {
            _log.Warn($"Host registration from {sourceIp} rejected: host-id '{register.HostId}' already claimed by another key");
            await RelayFrameCodec.WriteFrameAsync(stream, new RegisterAckFrame(false, "host id already claimed"), ct).ConfigureAwait(false);
            return;
        }

        await RelayFrameCodec.WriteFrameAsync(stream, new RegisterAckFrame(true, null), ct).ConfigureAwait(false);
        _log.Info($"Host '{register.HostId}' registered from {sourceIp}");

        try
        {
            // Control loop: the only inbound control frames are ban signals from the host (the auth authority).
            while (!ct.IsCancellationRequested)
            {
                RelayFrame? control = await RelayFrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (control is null)
                {
                    break;
                }

                if (control is BanSourceFrame ban)
                {
                    _rateLimiter.Ban(ban.SourceIp);
                    _log.Info($"Host '{register.HostId}' banned source {ban.SourceIp}");
                }
            }
        }
        finally
        {
            // Only remove the catalogue entry if it is still THIS registration (not a reconnect that replaced us).
            _hosts.TryRemove(new KeyValuePair<string, HostRegistration>(register.HostId, registration));
            _log.Info($"Host '{register.HostId}' control connection closed");
        }
    }

    // ---- Client connection -------------------------------------------------------------------

    /// <summary>Returns true if ownership of <paramref name="tcp"/> was transferred to the splice.</summary>
    private async Task<bool> HandleClientAsync(TcpClient tcp, Stream stream, string sourceIp, ClientRouteFrame route, CancellationToken ct)
    {
        if (!_hosts.TryGetValue(route.HostId, out HostRegistration? host))
        {
            _rateLimiter.AllowUnknownHostLookup(sourceIp); // record probe; tightens future lookups from this IP.
            _log.Warn($"Client from {sourceIp} routed to unknown host-id '{route.HostId}'");
            await RelayFrameCodec.WriteFrameAsync(stream, new RouteAckFrame(false, "unknown host"), ct).ConfigureAwait(false);
            return false;
        }

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pending = new PendingClient(tcp);
        _pending[token] = pending;
        try
        {
            await host.SendControlAsync(new ClientWaitingFrame(token, sourceIp), ct).ConfigureAwait(false);
            await RelayFrameCodec.WriteFrameAsync(stream, new RouteAckFrame(true, null), ct).ConfigureAwait(false);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(_options.DataConnectTimeout);
            (TcpClient hostTcp, Stream hostStream) = await pending.HostArrived.Task
                .WaitAsync(waitCts.Token).ConfigureAwait(false);

            // Both sides paired: splice and blind-forward. This handler now owns disposal of both.
            using (tcp)
            using (hostTcp)
            {
                _log.Info($"Splicing client {sourceIp} <-> host '{route.HostId}'");
                (long up, long down) = await SpliceAsync(stream, hostStream, ct).ConfigureAwait(false);
                _log.Info($"Closed client {sourceIp} <-> host '{route.HostId}' (client->host {up}B, host->client {down}B)");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"Client {sourceIp} for host '{route.HostId}' timed out waiting for host data connection");
            return false;
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<string, PendingClient>(token, pending));
        }
    }

    // ---- Host data connection ----------------------------------------------------------------

    /// <summary>Returns true if ownership of <paramref name="tcp"/> was handed to the waiting client's splice.</summary>
    private bool HandleHostData(TcpClient tcp, Stream stream, HostDataFrame data)
    {
        if (_pending.TryRemove(data.Token, out PendingClient? pending)
            && pending.HostArrived.TrySetResult((tcp, stream)))
        {
            return true; // the client handler now owns this connection.
        }

        _log.Warn("Host data connection presented an unknown/expired token");
        return false;
    }

    // ---- Blind forwarding --------------------------------------------------------------------

    /// <summary>
    /// Copies bytes in both directions until either side closes, then tears the other down. This is
    /// the ONLY thing that touches payload bytes, and it merely measures their length — it does not
    /// parse, buffer-inspect, or log any content. That is the blindness invariant, by construction.
    /// </summary>
    private static async Task<(long ClientToHost, long HostToClient)> SpliceAsync(Stream client, Stream host, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long clientToHost = 0;
        long hostToClient = 0;

        async Task Pump(Stream from, Stream to, bool clientDirection)
        {
            try
            {
                long n = await CopyCountingAsync(from, to, linked.Token).ConfigureAwait(false);
                if (clientDirection)
                {
                    clientToHost = n;
                }
                else
                {
                    hostToClient = n;
                }
            }
            finally
            {
                linked.Cancel(); // one direction ended → unblock and close the other.
            }
        }

        try
        {
            await Task.WhenAll(Pump(client, host, true), Pump(host, client, false)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Expected when one side closes and we cancel the other mid-copy.
        }

        return (clientToHost, hostToClient);
    }

    private static async Task<long> CopyCountingAsync(Stream from, Stream to, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                // Shutting down; the accept loop unwinding is expected.
            }
        }

        foreach (PendingClient pending in _pending.Values)
        {
            pending.Tcp.Dispose();
        }

        _cts?.Dispose();
    }

    /// <summary>A registered host and its control connection (with serialized frame writes).</summary>
    private sealed class HostRegistration(string hostId, byte[] spki, Stream controlStream)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public string HostId { get; } = hostId;

        public byte[] Spki { get; } = spki;

        public async Task SendControlAsync(RelayFrame frame, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RelayFrameCodec.WriteFrameAsync(controlStream, frame, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    /// <summary>A client connection parked until its host opens the paired data connection.</summary>
    private sealed class PendingClient(TcpClient tcp)
    {
        public TcpClient Tcp { get; } = tcp;

        public TaskCompletionSource<(TcpClient Tcp, Stream Stream)> HostArrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
