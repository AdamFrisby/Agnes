using Agnes.Abstractions.Events;
using Agnes.Host.Display;
using Agnes.Protocol;

namespace Agnes.Host.Tests.Display;

/// <summary>
/// Lazy creation and idle collapse — the two behaviours that keep a graphical session from costing anything
/// while nobody is looking at it, and the one that must NOT fire while an agent is mid-turn.
/// </summary>
public class DisplayBrokerRegistryTests
{
    private const string Session = DisplayFixture.Session;

    private static (DisplayBrokerRegistry Registry, StubSessionSource Sessions, StubDisplaySource Source, ManualClock Clock)
        Build()
    {
        var sessions = new StubSessionSource();
        var source = DisplayFixture.NewSource(sessions);
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var registry = new DisplayBrokerRegistry(
            sessions, DisplayFixture.Options(), new EventBus(), new RecordingControlSink(), loggers: null, time: clock);
        return (registry, sessions, source, clock);
    }

    [Fact]
    public async Task Nothing_is_captured_until_a_consumer_asks()
    {
        var (registry, _, source, _) = Build();
        await using var _r = registry;

        Assert.Null(registry.Find(Session));
        Assert.Equal(0, source.Opens);

        var broker = await registry.GetOrCreateAsync(Session);
        Assert.Same(broker, registry.Find(Session));
        Assert.Equal(1, source.Opens);
    }

    [Fact]
    public async Task Every_consumer_shares_the_one_capture_connection()
    {
        var (registry, _, source, _) = Build();
        await using var _r = registry;

        var a = await registry.GetOrCreateAsync(Session);
        var b = await registry.GetOrCreateAsync(Session);

        Assert.Same(a, b);
        Assert.Equal(1, source.Opens);
    }

    [Fact]
    public async Task A_session_with_no_display_is_refused_rather_than_faked()
    {
        var (registry, _, _, _) = Build();
        await using var _r = registry;

        Assert.False(registry.HasDisplay("headless"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetOrCreateAsync("headless"));
        Assert.Equal("This session has no display.", refused.Message);
    }

    [Fact]
    public async Task An_unused_broker_collapses_and_closes_the_capture()
    {
        var (registry, _, source, clock) = Build();
        await using var _r = registry;

        await registry.GetOrCreateAsync(Session);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await registry.SweepAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(registry.Find(Session));
        Assert.True(source.Session.Disposed);
    }

    [Fact]
    public async Task A_watched_broker_survives_the_sweep()
    {
        var (registry, _, _, clock) = Build();
        await using var _r = registry;

        var broker = await registry.GetOrCreateAsync(Session);
        broker.AddSubscriber(new DisplayQuality(64, 30, 60));
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, await registry.SweepAsync(TimeSpan.FromSeconds(30)));
        Assert.NotNull(registry.Find(Session));
    }

    [Fact]
    public async Task A_broker_survives_the_sweep_while_the_agent_is_mid_turn()
    {
        var (registry, sessions, _, clock) = Build();
        await using var _r = registry;

        await registry.GetOrCreateAsync(Session);
        sessions.ActiveTurns.Add(Session);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, await registry.SweepAsync(TimeSpan.FromSeconds(30)));

        // Turn over, nobody watching: now it goes.
        sessions.ActiveTurns.Remove(Session);
        Assert.Equal(1, await registry.SweepAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task The_sweep_also_expires_a_stale_human_hold()
    {
        var (registry, sessions, _, clock) = Build();
        await using var _r = registry;

        var broker = await registry.GetOrCreateAsync(Session);
        broker.AddSubscriber(new DisplayQuality(64, 30, 60)); // keeps it alive across the sweep
        await broker.RequestControlAsync("device-a", take: true);

        clock.Advance(TimeSpan.FromSeconds(120));
        await registry.SweepAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Agnes.Abstractions.DisplayControlHolder.None, broker.Arbiter.Holder);
    }
}
