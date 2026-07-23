using System.Security.Cryptography;
using Agnes.Relay;
using Agnes.Relay.Protocol;

namespace Agnes.Relay.Tests;

public sealed class RelayServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static (RelayServer Relay, RelayRateLimiter Limiter) StartRelay(
        IEnumerable<byte[]> authorizedKeys, RelayRateLimitOptions? rateLimit = null, TimeProvider? time = null)
    {
        var options = new RelayOptions
        {
            ListenAddress = "127.0.0.1",
            Port = 0,
            RateLimit = rateLimit ?? new RelayRateLimitOptions(),
            DataConnectTimeout = TimeSpan.FromSeconds(5),
        };
        var limiter = new RelayRateLimiter(options.RateLimit, time);
        var relay = new RelayServer(options, new InMemoryAuthorizedHostKeys(authorizedKeys), limiter, timeProvider: time);
        relay.Start();
        return (relay, limiter);
    }

    [Fact]
    public async Task Client_is_spliced_to_host_and_bytes_round_trip_both_directions()
    {
        using var key = new TestHostKey();
        (RelayServer relay, _) = StartRelay([key.Spki]);
        await using var _r = relay;

        byte[] clientToHost = RandomNumberGenerator.GetBytes(4096);
        byte[] hostToClient = RandomNumberGenerator.GetBytes(4096);
        var receivedByHost = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = new RelayTestHost(relay.Port, key, "host-A");
        host.OnDataConnection = async stream =>
        {
            byte[] buf = new byte[clientToHost.Length];
            await stream.ReadExactlyAsync(buf);
            receivedByHost.TrySetResult(buf);
            await stream.WriteAsync(hostToClient);
            await stream.FlushAsync();
        };
        RegisterAckFrame reg = await host.RegisterAsync();
        Assert.True(reg.Ok);

        (RouteAckFrame ack, var tcp, var stream) = await RelayTestClient.RouteAsync(relay.Port, "host-A");
        using (tcp)
        {
            Assert.True(ack.Ok);
            await stream.WriteAsync(clientToHost);
            await stream.FlushAsync();

            byte[] back = new byte[hostToClient.Length];
            await stream.ReadExactlyAsync(back);

            byte[] hostGot = await receivedByHost.Task.WaitAsync(Timeout);
            Assert.Equal(clientToHost, hostGot);   // client -> host arrived byte-identical
            Assert.Equal(hostToClient, back);       // host -> client arrived byte-identical
        }
    }

    [Fact]
    public async Task Blind_forwarding_of_arbitrary_non_tls_bytes_is_byte_identical()
    {
        using var key = new TestHostKey();
        (RelayServer relay, _) = StartRelay([key.Spki]);
        await using var _r = relay;

        // A large payload spanning every byte value and multiple internal copy buffers — nothing that
        // resembles a TLS record or a relay frame. If the relay parsed anything, this would corrupt/stall.
        byte[] payload = new byte[200_000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31) ^ (i >> 3));
        }

        var echoed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new RelayTestHost(relay.Port, key, "H");
        host.OnDataConnection = async stream =>
        {
            byte[] buf = new byte[payload.Length];
            await stream.ReadExactlyAsync(buf);
            echoed.TrySetResult(buf);
        };
        Assert.True((await host.RegisterAsync()).Ok);

        (RouteAckFrame ack, var tcp, var stream) = await RelayTestClient.RouteAsync(relay.Port, "H");
        using (tcp)
        {
            Assert.True(ack.Ok);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            byte[] got = await echoed.Task.WaitAsync(Timeout);
            Assert.Equal(payload, got);
        }
    }

    [Fact]
    public async Task Client_for_unknown_host_is_rejected()
    {
        using var key = new TestHostKey();
        (RelayServer relay, _) = StartRelay([key.Spki]);
        await using var _r = relay;

        RouteAckFrame? ack = await RelayTestClient.RouteRawAsync(relay.Port, "no-such-host");
        Assert.NotNull(ack);
        Assert.False(ack!.Ok);
    }

    [Fact]
    public async Task Host_with_unauthorized_key_is_rejected()
    {
        using var authorized = new TestHostKey();
        using var stranger = new TestHostKey();
        (RelayServer relay, _) = StartRelay([authorized.Spki]); // stranger NOT on the allow-list
        await using var _r = relay;

        await using var host = new RelayTestHost(relay.Port, stranger, "H");
        RegisterAckFrame ack = await host.RegisterAsync();
        Assert.False(ack.Ok);
    }

    [Fact]
    public async Task Second_key_cannot_claim_an_already_claimed_host_id()
    {
        using var keyA = new TestHostKey();
        using var keyB = new TestHostKey();
        (RelayServer relay, _) = StartRelay([keyA.Spki, keyB.Spki]); // both authorized
        await using var _r = relay;

        await using var hostA = new RelayTestHost(relay.Port, keyA, "shared-id");
        Assert.True((await hostA.RegisterAsync()).Ok);

        await using var hostB = new RelayTestHost(relay.Port, keyB, "shared-id");
        RegisterAckFrame ackB = await hostB.RegisterAsync();
        Assert.False(ackB.Ok); // squatting refused
    }

    [Fact]
    public async Task Same_key_may_reclaim_its_own_host_id()
    {
        using var key = new TestHostKey();
        (RelayServer relay, _) = StartRelay([key.Spki]);
        await using var _r = relay;

        await using var first = new RelayTestHost(relay.Port, key, "H");
        Assert.True((await first.RegisterAsync()).Ok);

        await using var again = new RelayTestHost(relay.Port, key, "H");
        Assert.True((await again.RegisterAsync()).Ok); // same key -> allowed to reconnect
    }

    [Fact]
    public async Task Rate_limit_trips_after_a_burst_and_resets_on_the_next_window()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var rl = new RelayRateLimitOptions { PerIpPerMinute = 5, MaxConcurrentPerIp = 100, UnknownHostPerIpPerMinute = 1000 };
        (RelayServer relay, _) = StartRelay([], rl, clock);
        await using var _r = relay;

        int accepted = 0;
        int dropped = 0;
        for (int i = 0; i < 8; i++)
        {
            RouteAckFrame? ack = await RelayTestClient.RouteRawAsync(relay.Port, "whatever");
            if (ack is null)
            {
                dropped++;
            }
            else
            {
                accepted++;
            }
        }

        Assert.Equal(5, accepted);   // exactly the per-window budget was let through
        Assert.Equal(3, dropped);    // the rest were dropped at accept

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        RouteAckFrame? afterReset = await RelayTestClient.RouteRawAsync(relay.Port, "whatever");
        Assert.NotNull(afterReset); // new window -> allowed again
    }

    [Fact]
    public async Task Host_signaled_ban_blocks_the_source_next_connection()
    {
        using var key = new TestHostKey();
        (RelayServer relay, RelayRateLimiter limiter) = StartRelay([key.Spki]);
        await using var _r = relay;

        await using var host = new RelayTestHost(relay.Port, key, "H");
        Assert.True((await host.RegisterAsync()).Ok);

        await host.BanAsync("127.0.0.1");

        // The ban travels over the control channel and is applied asynchronously — wait for it to land.
        using var cts = new CancellationTokenSource(Timeout);
        while (!limiter.IsBanned("127.0.0.1"))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }

        RouteAckFrame? ack = await RelayTestClient.RouteRawAsync(relay.Port, "H");
        Assert.Null(ack); // dropped at accept because the source is banned
    }
}
