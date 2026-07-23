using System.Net.Sockets;
using System.Security.Cryptography;
using Agnes.Relay;
using Agnes.Relay.Protocol;

namespace Agnes.Relay.Tests;

/// <summary>A P-256 host key pair for tests: exposes its SPKI + can sign a relay challenge.</summary>
public sealed class TestHostKey : IDisposable
{
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] Spki => _ecdsa.ExportSubjectPublicKeyInfo();

    public string PublicKeyB64 => Convert.ToBase64String(Spki);

    public string Sign(string nonce, string hostId)
    {
        byte[] sig = _ecdsa.SignData(HostKeyVerifier.ChallengePayload(nonce, hostId),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(sig);
    }

    public void Dispose() => _ecdsa.Dispose();
}

/// <summary>A deterministic clock for rate-limit window/ban-expiry tests.</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// An in-process host: opens a persistent control connection, registers with its per-host key, then
/// answers "client waiting" signals by opening data connections and running a supplied handler on the
/// (post-handshake, opaque) stream. Mirrors what the real host-side relay transport would do.
/// </summary>
public sealed class RelayTestHost(int port, TestHostKey key, string hostId) : IAsyncDisposable
{
    private TcpClient? _control;
    private Stream? _controlStream;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Handler invoked for each spliced data connection (the "server" side of the tunnel).</summary>
    public Func<Stream, Task>? OnDataConnection { get; set; }

    /// <summary>Opens the control connection and attempts registration; returns the relay's ack.</summary>
    public async Task<RegisterAckFrame> RegisterAsync(CancellationToken ct = default)
    {
        _control = new TcpClient();
        await _control.ConnectAsync("127.0.0.1", port, ct);
        _controlStream = _control.GetStream();

        await RelayFrameCodec.WriteFrameAsync(_controlStream, new HostHelloFrame(1), ct);
        var challenge = (ChallengeFrame)(await RelayFrameCodec.ReadFrameAsync(_controlStream, ct))!;
        await RelayFrameCodec.WriteFrameAsync(_controlStream,
            new HostRegisterFrame(hostId, key.PublicKeyB64, key.Sign(challenge.Nonce, hostId)), ct);
        var ack = (RegisterAckFrame)(await RelayFrameCodec.ReadFrameAsync(_controlStream, ct))!;

        if (ack.Ok)
        {
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ControlLoopAsync(_cts.Token));
        }

        return ack;
    }

    /// <summary>Signal the relay to ban a source IP over the control channel.</summary>
    public async Task BanAsync(string sourceIp, CancellationToken ct = default) =>
        await RelayFrameCodec.WriteFrameAsync(_controlStream!, new BanSourceFrame(sourceIp), ct);

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                RelayFrame? frame = await RelayFrameCodec.ReadFrameAsync(_controlStream!, ct);
                if (frame is null)
                {
                    break;
                }

                if (frame is ClientWaitingFrame waiting)
                {
                    _ = Task.Run(() => OpenDataConnectionAsync(waiting.Token, ct), CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Control connection closing during teardown is expected.
        }
    }

    private async Task OpenDataConnectionAsync(string token, CancellationToken ct)
    {
        using var data = new TcpClient();
        await data.ConnectAsync("127.0.0.1", port, ct);
        Stream stream = data.GetStream();
        await RelayFrameCodec.WriteFrameAsync(stream, new HostDataFrame(token), ct);
        if (OnDataConnection is { } handler)
        {
            await handler(stream);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _control?.Dispose();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Expected during teardown.
            }
        }
    }
}

/// <summary>Helpers for the in-process client side of a relay tunnel.</summary>
public static class RelayTestClient
{
    /// <summary>
    /// Connects, sends the routing header for <paramref name="hostId"/>, and reads the ack. On success
    /// the returned stream carries opaque bytes to/from the paired host; the caller owns disposal.
    /// </summary>
    public static async Task<(RouteAckFrame Ack, TcpClient Tcp, Stream Stream)> RouteAsync(
        int port, string hostId, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, ct);
        tcp.NoDelay = true;
        Stream stream = tcp.GetStream();
        await RelayFrameCodec.WriteFrameAsync(stream, new ClientRouteFrame(hostId), ct);
        RelayFrame? ack = await RelayFrameCodec.ReadFrameAsync(stream, ct);
        return ((RouteAckFrame)ack!, tcp, stream);
    }

    /// <summary>
    /// Connects and requests a route, returning the ack frame — or <c>null</c> if the relay dropped the
    /// connection outright (rate-limited / banned before it ever answered). Disposes its own socket.
    /// </summary>
    public static async Task<RouteAckFrame?> RouteRawAsync(int port, string hostId, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port, ct);
            Stream stream = tcp.GetStream();
            await RelayFrameCodec.WriteFrameAsync(stream, new ClientRouteFrame(hostId), ct);
            return (RouteAckFrame?)await RelayFrameCodec.ReadFrameAsync(stream, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return null; // connection reset by a rate-limit/ban drop.
        }
    }
}
