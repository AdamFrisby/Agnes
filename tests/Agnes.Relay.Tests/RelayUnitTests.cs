using Agnes.Relay;
using Agnes.Relay.Protocol;

namespace Agnes.Relay.Tests;

public sealed class RelayFrameCodecTests
{
    [Fact]
    public async Task Frames_round_trip_through_the_length_prefixed_codec()
    {
        RelayFrame[] frames =
        [
            new HostHelloFrame(1),
            new ChallengeFrame("nonce-abc"),
            new HostRegisterFrame("host-1", "pubkey", "sig"),
            new RegisterAckFrame(true, null),
            new ClientWaitingFrame("token-xyz", "10.0.0.7"),
            new BanSourceFrame("10.0.0.9"),
            new ClientRouteFrame("host-1"),
            new RouteAckFrame(false, "unknown host"),
            new HostDataFrame("token-xyz"),
        ];

        using var buffer = new MemoryStream();
        foreach (RelayFrame frame in frames)
        {
            await RelayFrameCodec.WriteFrameAsync(buffer, frame);
        }

        buffer.Position = 0;
        foreach (RelayFrame expected in frames)
        {
            RelayFrame? actual = await RelayFrameCodec.ReadFrameAsync(buffer);
            Assert.Equal(expected, actual); // record value-equality, preserves concrete type + fields
        }

        Assert.Null(await RelayFrameCodec.ReadFrameAsync(buffer)); // clean EOF at the frame boundary
    }

    [Fact]
    public async Task Read_does_not_over_consume_trailing_opaque_bytes()
    {
        using var buffer = new MemoryStream();
        await RelayFrameCodec.WriteFrameAsync(buffer, new ClientRouteFrame("H"));
        byte[] trailing = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01];
        await buffer.WriteAsync(trailing);
        buffer.Position = 0;

        RelayFrame? frame = await RelayFrameCodec.ReadFrameAsync(buffer);
        Assert.Equal(new ClientRouteFrame("H"), frame);

        // The opaque bytes after the frame are untouched — exactly the property that lets the relay
        // hand the raw stream straight to blind forwarding.
        byte[] rest = new byte[trailing.Length];
        await buffer.ReadExactlyAsync(rest);
        Assert.Equal(trailing, rest);
    }
}

public sealed class RelayRateLimiterTests
{
    [Fact]
    public void Per_ip_window_limits_then_resets_with_the_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var rl = new RelayRateLimiter(new RelayRateLimitOptions { PerIpPerMinute = 3, MaxConcurrentPerIp = 100 }, clock);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(rl.TryAcceptConnection("1.2.3.4"));
            rl.ReleaseConnection("1.2.3.4");
        }

        Assert.False(rl.TryAcceptConnection("1.2.3.4")); // window exhausted
        Assert.True(rl.TryAcceptConnection("5.6.7.8"));  // different IP unaffected

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.True(rl.TryAcceptConnection("1.2.3.4"));  // window reset
    }

    [Fact]
    public void Concurrency_cap_is_enforced_and_released()
    {
        var rl = new RelayRateLimiter(new RelayRateLimitOptions { PerIpPerMinute = 1000, MaxConcurrentPerIp = 2 });

        Assert.True(rl.TryAcceptConnection("ip"));
        Assert.True(rl.TryAcceptConnection("ip"));
        Assert.False(rl.TryAcceptConnection("ip")); // 2 concurrent already

        rl.ReleaseConnection("ip");
        Assert.True(rl.TryAcceptConnection("ip")); // a slot freed
    }

    [Fact]
    public void Ban_blocks_then_expires_on_the_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var rl = new RelayRateLimiter(
            new RelayRateLimitOptions { PerIpPerMinute = 1000, MaxConcurrentPerIp = 100, BanDuration = TimeSpan.FromMinutes(10) },
            clock);

        rl.Ban("9.9.9.9");
        Assert.True(rl.IsBanned("9.9.9.9"));
        Assert.False(rl.TryAcceptConnection("9.9.9.9"));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(rl.IsBanned("9.9.9.9"));
        Assert.True(rl.TryAcceptConnection("9.9.9.9"));
    }

    [Fact]
    public void Unknown_host_lookups_are_separately_rate_limited()
    {
        var rl = new RelayRateLimiter(new RelayRateLimitOptions { UnknownHostPerIpPerMinute = 2 });
        Assert.True(rl.AllowUnknownHostLookup("ip"));
        Assert.True(rl.AllowUnknownHostLookup("ip"));
        Assert.False(rl.AllowUnknownHostLookup("ip"));
    }
}

public sealed class AuthorizedHostKeysTests
{
    [Fact]
    public void File_backed_allow_list_matches_listed_keys_only()
    {
        using var listed = new TestHostKey();
        using var other = new TestHostKey();

        string path = Path.Combine(Path.GetTempPath(), $"agnes-relay-keys-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, ["# authorized hosts", $"{listed.PublicKeyB64} my-laptop"]);
        try
        {
            var keys = new FileAuthorizedHostKeys(path);
            Assert.True(keys.IsAuthorized(listed.Spki));
            Assert.False(keys.IsAuthorized(other.Spki));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Verify_accepts_a_valid_signature_and_rejects_a_wrong_host_id()
    {
        using var key = new TestHostKey();
        var authorized = new InMemoryAuthorizedHostKeys([key.Spki]);
        const string nonce = "the-nonce";

        string sig = key.Sign(nonce, "host-1");
        Assert.NotNull(HostKeyVerifier.Verify(authorized, nonce, "host-1", key.PublicKeyB64, sig));
        // Same signature, different host-id -> fails (the id is bound into the signed payload).
        Assert.Null(HostKeyVerifier.Verify(authorized, nonce, "host-2", key.PublicKeyB64, sig));
    }
}
