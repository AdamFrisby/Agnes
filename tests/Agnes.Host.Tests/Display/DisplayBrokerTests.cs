using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Display;
using Agnes.Protocol;
using Agnes.Sandbox;

namespace Agnes.Host.Tests.Display;

/// <summary>
/// The broker's three jobs, each asserted on its own: turn guest damage into the right <em>kind</em> of frame
/// at the right size, never let a slow client grow a queue, and hand control between the agent and a person
/// without either of them losing a keystroke to the other.
/// <para>Everything here runs against <see cref="StubDisplaySource"/> — no VM, no capture, no encoder
/// dependency beyond the real SkiaSharp one, which is exercised deliberately: a JPEG that doesn't encode is
/// the single failure that would make the whole channel useless.</para>
/// </summary>
public class DisplayBrokerTests
{
    /// <summary>Waits for the capture pump to hand the subscriber some damage; the pump is a real background
    /// task, so the tests synchronize on its effect rather than on a sleep.</summary>
    private static async Task<(DisplayRect Damage, IReadOnlyList<DisplayControlNotice> Notices)> NextAsync(
        DisplaySubscriber subscriber)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await subscriber.TakeAsync(timeout.Token);
    }

    [Fact]
    public async Task Small_damage_becomes_one_tile_at_its_own_position()
    {
        var (broker, source, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var subscriber = broker.AddSubscriber(new DisplayQuality(64, 60, 60));
        await NextAsync(subscriber); // the seeded full frame every joining client gets.

        source.Session.Paint(8, 4, 8, 6);
        var (damage, _) = await NextAsync(subscriber);

        var frame = broker.BuildFrame(subscriber, damage);
        Assert.NotNull(frame);
        Assert.Equal(DisplayFrameKind.Tile, frame!.Value.Header.Kind);
        Assert.Equal(8, frame.Value.Header.X);
        Assert.Equal(4, frame.Value.Header.Y);
        Assert.Equal(8, frame.Value.Header.Width);
        Assert.Equal(6, frame.Value.Header.Height);

        // Whatever the tile's own size, the header states the GUEST geometry — that is what a client maps
        // clicks through.
        Assert.Equal(64, frame.Value.Header.DisplayWidth);
        Assert.Equal(48, frame.Value.Header.DisplayHeight);
        Assert.True(frame.Value.Payload.Length > 0);
        Assert.Equal((uint)frame.Value.Payload.Length, frame.Value.Header.PayloadLength);
    }

    [Fact]
    public async Task Damage_over_the_threshold_becomes_one_full_frame()
    {
        var (broker, source, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var subscriber = broker.AddSubscriber(new DisplayQuality(64, 60, 60));
        await NextAsync(subscriber);

        // 64×48 = 3072 px; 40% is 1228.8. A 40×40 rectangle is 1600 px — over the line.
        source.Session.Paint(0, 0, 40, 40);
        var (damage, _) = await NextAsync(subscriber);

        var frame = broker.BuildFrame(subscriber, damage)!.Value;
        Assert.Equal(DisplayFrameKind.Full, frame.Header.Kind);
        Assert.Equal(0, frame.Header.X);
        Assert.Equal(64, frame.Header.Width);
        Assert.Equal(48, frame.Header.Height);
    }

    [Fact]
    public async Task Separate_damaged_regions_coalesce_into_one_frame()
    {
        var (broker, source, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var subscriber = broker.AddSubscriber(new DisplayQuality(64, 60, 60));
        await NextAsync(subscriber);

        // Two small, far-apart repaints. Their union is 2×2 at (2,2) through (12,12) — still small enough to
        // stay a tile, which is the point: one frame covers both, positioned over both.
        source.Session.Paint(2, 2, 2, 2);
        source.Session.Paint(10, 10, 2, 2);

        DisplayRect damage = default;
        // Both updates may or may not land in the same take; keep unioning until they have.
        while (damage.Width < 10)
        {
            var (next, _) = await NextAsync(subscriber);
            damage = damage.Union(next);
        }

        var frame = broker.BuildFrame(subscriber, damage)!.Value;
        Assert.Equal(DisplayFrameKind.Tile, frame.Header.Kind);
        Assert.Equal(2, frame.Header.X);
        Assert.Equal(2, frame.Header.Y);
        Assert.Equal(10, frame.Header.Width);
        Assert.Equal(10, frame.Header.Height);
    }

    [Fact]
    public async Task A_subscriber_max_width_scales_the_frame_but_not_the_stated_geometry()
    {
        var (broker, source, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        // Half width: a full frame's pixels halve, the header's display size does not.
        var subscriber = broker.AddSubscriber(new DisplayQuality(32, 60, 60));
        var (damage, _) = await NextAsync(subscriber);

        var frame = broker.BuildFrame(subscriber, damage)!.Value;
        Assert.Equal(DisplayFrameKind.Full, frame.Header.Kind);
        Assert.Equal(64, frame.Header.DisplayWidth);
        Assert.Equal(48, frame.Header.DisplayHeight);

        // The header's Width/Height are the region in guest pixels; the JPEG behind it is the scaled one.
        Assert.Equal(64, frame.Header.Width);
        using var decoded = SkiaSharp.SKBitmap.Decode(frame.Payload);
        Assert.Equal(32, decoded.Width);
        Assert.Equal(24, decoded.Height);
    }

    [Fact]
    public async Task A_slow_subscriber_drops_frames_instead_of_queueing_them()
    {
        var (broker, source, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var subscriber = broker.AddSubscriber(new DisplayQuality(64, 60, 60));
        await NextAsync(subscriber);

        // Nobody is reading. Thirty repaints must not become thirty pending frames.
        for (var i = 0; i < 30; i++)
        {
            source.Session.Paint(i % 20, 0, 2, 2);
        }

        // Wait for the pump to have delivered them all; each one that lands on an unread slot is a drop.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (subscriber.DroppedFrames < 20 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(subscriber.DroppedFrames >= 20, "a subscriber that never read should record coalesced frames");

        // One take drains everything that accumulated — the mailbox is one slot deep by construction.
        var (damage, _) = await NextAsync(subscriber);
        Assert.False(damage.IsEmpty);

        // And it is genuinely empty afterwards: the next take blocks rather than yielding a backlog.
        using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscriber.TakeAsync(idle.Token));
    }

    [Fact]
    public async Task No_guest_update_means_no_frame()
    {
        var (broker, _, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var subscriber = broker.AddSubscriber(new DisplayQuality(64, 60, 60));
        await NextAsync(subscriber); // the join frame

        using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscriber.TakeAsync(idle.Token));
    }

    [Fact]
    public async Task A_screenshot_is_a_jpeg_of_the_whole_surface()
    {
        var (broker, _, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var jpeg = await broker.ScreenshotJpegAsync(maxWidth: null, quality: 70);
        using var decoded = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(48, decoded.Height);

        var scaled = await broker.ScreenshotJpegAsync(maxWidth: 32, quality: 70);
        using var decodedScaled = SkiaSharp.SKBitmap.Decode(scaled);
        Assert.Equal(32, decodedScaled.Width);
    }

    [Fact]
    public async Task A_burst_tiles_into_one_contact_sheet()
    {
        var (broker, _, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var frames = await broker.BurstJpegAsync(4, TimeSpan.FromMilliseconds(20), maxWidth: null, quality: 60);
        Assert.Equal(4, frames.Count);

        var sheet = DisplayJpeg.ContactSheet(frames, 60);
        Assert.Equal(2, sheet.Columns);
        Assert.Equal(2, sheet.Rows);
        using var decoded = SkiaSharp.SKBitmap.Decode(sheet.Jpeg);
        Assert.Equal(64 * 2 + 2, decoded.Width);   // two cells plus the gutter
        Assert.Equal(48 * 2 + 2, decoded.Height);
    }

    // ---- the arbiter, through the broker ----

    [Fact]
    public async Task The_agent_takes_control_implicitly_and_the_handover_is_logged_and_pushed()
    {
        var (broker, source, sink, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        var subscriber = broker.AddSubscriber(new DisplayQuality(64, 60, 60));
        await NextAsync(subscriber);

        await broker.InjectAgentAsync([new PointerMove(5, 6)]);

        Assert.Equal(DisplayControlHolder.Agent, broker.Arbiter.Holder);
        Assert.Equal(new PointerMove(5, 6), Assert.Single(source.Session.Snapshot()));

        var logged = Assert.Single(sink.Snapshot());
        Assert.Equal(DisplayControlHolder.Agent, logged.Holder);
        Assert.Null(logged.DeviceId);

        var (_, notices) = await NextAsync(subscriber);
        Assert.Equal(DisplayControlHolder.Agent, Assert.Single(notices).Holder);
    }

    [Fact]
    public async Task A_person_taking_control_locks_the_agent_out_and_is_logged()
    {
        var (broker, source, sink, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        await broker.RequestControlAsync("device-a", take: true);
        Assert.Equal(DisplayControlHolder.User, broker.Arbiter.Holder);
        Assert.Equal("device-a", broker.Arbiter.HolderDeviceId);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.InjectAgentAsync([new PointerMove(1, 1)]));
        Assert.Equal(InputArbiter.UserHoldsMessage, refused.Message);
        Assert.Empty(source.Session.Snapshot());

        var logged = Assert.Single(sink.Snapshot());
        Assert.Equal(DisplayControlHolder.User, logged.Holder);
        Assert.Equal("device-a", logged.DeviceId);
    }

    [Fact]
    public async Task A_person_input_only_lands_for_the_device_that_holds_the_display()
    {
        var (broker, source, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        Assert.False(await broker.InjectUserAsync(new PointerMove(1, 1), "device-a"));
        Assert.Empty(source.Session.Snapshot());

        await broker.RequestControlAsync("device-a", take: true);
        Assert.True(await broker.InjectUserAsync(new PointerMove(2, 3), "device-a"));
        Assert.False(await broker.InjectUserAsync(new PointerMove(9, 9), "device-b"));
        Assert.Equal(new PointerMove(2, 3), Assert.Single(source.Session.Snapshot()));
    }

    [Fact]
    public async Task A_closed_channel_releases_the_hold_it_was_carrying()
    {
        var (broker, _, sink, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        await broker.RequestControlAsync("device-a", take: true);
        await broker.ReleaseControlForAsync("device-a");

        Assert.Equal(DisplayControlHolder.None, broker.Arbiter.Holder);
        Assert.Equal(
            new[] { DisplayControlHolder.User, DisplayControlHolder.None },
            sink.Snapshot().Select(e => e.Holder).ToArray());

        // And the agent may drive again.
        await broker.InjectAgentAsync([new PointerMove(1, 1)]);
        Assert.Equal(DisplayControlHolder.Agent, broker.Arbiter.Holder);
    }

    [Fact]
    public async Task A_vetoed_input_never_reaches_the_guest()
    {
        var bus = new EventBus();
        bus.Intercept(new VetoEverything());

        var sessions = new StubSessionSource();
        var source = DisplayFixture.NewSource(sessions);
        var session = await source.OpenDisplayAsync();
        await using var broker = new DisplayBroker(
            DisplayFixture.Session, session, source.Display, DisplayFixture.Options(), bus, new RecordingControlSink());
        await broker.StartAsync();

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.InjectAgentAsync([new PointerMove(1, 1)]));
        Assert.Contains("not on this host", blocked.Message, StringComparison.Ordinal);
        Assert.Empty(source.Session.Snapshot());

        // A person's vetoed input is dropped rather than thrown — there is nowhere to show it.
        await broker.RequestControlAsync("device-a", take: true);
        Assert.False(await broker.InjectUserAsync(new PointerMove(1, 1), "device-a"));
        Assert.Empty(source.Session.Snapshot());
    }

    private sealed class VetoEverything : IEventInterceptor<BeforeDisplayInputEvent>
    {
        public int Order => 0;

        public ValueTask InterceptAsync(BeforeDisplayInputEvent evt, CancellationToken cancellationToken = default)
        {
            evt.Cancel("not on this host");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_held_key_is_always_released()
    {
        var (broker, source, _, _) = await DisplayFixture.BrokerAsync();
        await using var _b = broker;

        await broker.InjectAgentHeldAsync(
            [new KeyPress("Down", Down: true)], TimeSpan.FromMilliseconds(20), [new KeyPress("Down", Down: false)]);

        Assert.Equal(
            new DisplayInput[] { new KeyPress("Down", true), new KeyPress("Down", false) },
            source.Session.Snapshot());
    }
}
