using System.Net;
using System.Net.Sockets;
using System.Text;
using Agnes.Host.Hosting;
using Agnes.Relay;
using Agnes.Relay.Protocol;

namespace Agnes.Host.Tests;

/// <summary>
/// The host relay transport (<see cref="AgnesRelayTransportProvider"/>) dials the blind relay out, registers
/// with its per-host key, and answers "client waiting" signals by opening a data connection it <b>blind-pumps</b>
/// to the host's own loopback listener. These stand up the real in-process relay broker + a loopback echo
/// "Kestrel" — no external network — and prove: the pump moves bytes verbatim (blindness), the advertised
/// address is the relay + host-id + pinned fingerprint (AC5), and misconfig/unauthorized fail loudly (AC6).
/// </summary>
public sealed class AgnesRelayTransportProviderTests
{
    private static async Task<int> ReserveClosedPortAsync()
    {
        // Bind then release a loopback port so a connect there is refused (a definitely-unreachable relay).
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        await Task.Yield();
        return port;
    }

    private static (RelayServer Relay, int Port) StartRelay(IAuthorizedHostKeys authorized)
    {
        var relay = new RelayServer(new RelayOptions { ListenAddress = "127.0.0.1", Port = 0 }, authorized);
        relay.Start();
        return (relay, relay.Port);
    }

    private static SelfSignedHostCertificateProvider TempCertProvider()
        => new(Path.Combine(Path.GetTempPath(), $"agnes-relay-cert-{Guid.NewGuid():n}.pfx"));

    [Fact]
    public async Task Exposes_and_blind_pumps_bytes_verbatim_to_the_local_listener()
    {
        // A loopback "local Kestrel" that just echoes — the pump must deliver bytes unchanged in both directions.
        using var echo = new LoopbackEcho();

        using var key = new InMemoryRelayHostKey();
        var (relay, relayPort) = StartRelay(new InMemoryAuthorizedHostKeys([key.Spki]));
        await using var relayLifetime = relay;

        using var cert = TempCertProvider();
        var options = new RelayTransportOptions { Url = $"127.0.0.1:{relayPort}", HostId = "host-abc" };
        await using var provider = new AgnesRelayTransportProvider(options, key, cert);

        TransportEndpoint endpoint = await provider.ExposeAsync(
            new HostExposureContext([$"https://127.0.0.1:{echo.Port}"]));

        // AC5: the advertised address is the relay + host-id + pinned fingerprint, never a LAN address.
        string address = Assert.Single(endpoint.ClientAddresses);
        Assert.StartsWith($"agnes-relay://127.0.0.1:{relayPort}/host-abc", address);
        Assert.Contains($"fp={cert.Fingerprint}", address, StringComparison.Ordinal);

        // Route a client through the relay and round-trip an opaque payload — proves the loopback pump is wired
        // and copies bytes verbatim (the blindness invariant: no TLS parsing, no mutation).
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort);
        Stream stream = client.GetStream();
        await RelayFrameCodec.WriteFrameAsync(stream, new ClientRouteFrame("host-abc"));
        var ack = Assert.IsType<RouteAckFrame>(await RelayFrameCodec.ReadFrameAsync(stream));
        Assert.True(ack.Ok);

        byte[] payload = Encoding.UTF8.GetBytes("the relay never sees this cleartext meaningfully — it is opaque");
        await stream.WriteAsync(payload);
        await stream.FlushAsync();

        byte[] received = new byte[payload.Length];
        await stream.ReadExactlyAsync(received);
        Assert.Equal(payload, received); // byte-for-byte identical after two hops (relay splice + host pump).
    }

    [Fact]
    public async Task Unreachable_relay_throws_a_clear_error_with_no_fallback()
    {
        int deadPort = await ReserveClosedPortAsync();
        using var key = new InMemoryRelayHostKey();
        using var cert = TempCertProvider();
        var options = new RelayTransportOptions { Url = $"127.0.0.1:{deadPort}", HostId = "host-abc" };
        await using var provider = new AgnesRelayTransportProvider(options, key, cert);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExposeAsync(new HostExposureContext(["https://127.0.0.1:5081"])));
        Assert.Contains("Could not reach the Agnes relay", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthorized_host_key_throws_a_clear_error_with_no_fallback()
    {
        // The relay authorizes some OTHER key, not this host's — registration must be rejected.
        using var otherKey = new InMemoryRelayHostKey();
        var (relay, relayPort) = StartRelay(new InMemoryAuthorizedHostKeys([otherKey.Spki]));
        await using var relayLifetime = relay;

        using var key = new InMemoryRelayHostKey();
        using var cert = TempCertProvider();
        var options = new RelayTransportOptions { Url = $"127.0.0.1:{relayPort}", HostId = "host-abc" };
        await using var provider = new AgnesRelayTransportProvider(options, key, cert);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExposeAsync(new HostExposureContext(["https://127.0.0.1:5081"])));
        Assert.Contains("rejected this host's registration", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_host_id_or_url_throws_before_dialing()
    {
        using var key = new InMemoryRelayHostKey();
        using var cert = TempCertProvider();

        await using var noHostId = new AgnesRelayTransportProvider(
            new RelayTransportOptions { Url = "127.0.0.1:1", HostId = "" }, key, cert);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => noHostId.ExposeAsync(new HostExposureContext(["https://127.0.0.1:5081"])));

        await using var noUrl = new AgnesRelayTransportProvider(
            new RelayTransportOptions { Url = "", HostId = "host-abc" }, key, cert);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => noUrl.ExposeAsync(new HostExposureContext(["https://127.0.0.1:5081"])));
    }

    [Fact]
    public void Provider_is_outbound_only_with_the_expected_id()
    {
        using var key = new InMemoryRelayHostKey();
        using var cert = TempCertProvider();
        var provider = new AgnesRelayTransportProvider(
            new RelayTransportOptions { Url = "127.0.0.1:5100", HostId = "h" }, key, cert);

        Assert.Equal("agnes-relay", provider.Id);
        Assert.True(provider.RequiresOutboundOnly);
    }

    /// <summary>A trivial loopback TCP echo server standing in for the host's local Kestrel listener.</summary>
    private sealed class LoopbackEcho : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public LoopbackEcho()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient conn = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (conn)
                        {
                            Stream s = conn.GetStream();
                            await s.CopyToAsync(s, _cts.Token);
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Shutting down.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
