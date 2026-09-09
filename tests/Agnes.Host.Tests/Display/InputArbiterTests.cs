using Agnes.Abstractions;
using Agnes.Host.Display;

namespace Agnes.Host.Tests.Display;

/// <summary>
/// The rules about who holds the mouse, tested directly against a hand-advanced clock. These are the
/// invariants the whole feature rests on: an agent never wrests control from a person, a person's claim
/// cannot outlive their attention, and neither of them can spend the guest's input queue without limit.
/// </summary>
public class InputArbiterTests
{
    private static (InputArbiter Arbiter, ManualClock Clock) Build(DisplayOptions? options = null)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new InputArbiter(options ?? new DisplayOptions(), clock), clock);
    }

    [Fact]
    public void The_agent_takes_control_from_nobody_but_not_from_a_person()
    {
        var (arbiter, _) = Build();

        Assert.Equal(DisplayControlHolder.Agent, arbiter.ClaimForAgent()!.Holder);
        Assert.Null(arbiter.ClaimForAgent()); // already holds it — one handover, not one per input.

        arbiter.RequestControl("device-a", take: true);
        var refused = Assert.Throws<InvalidOperationException>(() => arbiter.ClaimForAgent());
        Assert.Equal(InputArbiter.UserHoldsMessage, refused.Message);
    }

    [Fact]
    public void A_person_wins_immediately_even_mid_turn()
    {
        var (arbiter, _) = Build();
        arbiter.ClaimForAgent();

        var taken = arbiter.RequestControl("device-a", take: true);
        Assert.Equal(DisplayControlHolder.User, taken!.Holder);
        Assert.Equal("device-a", taken.DeviceId);
        Assert.False(arbiter.AgentMayInject);
    }

    [Fact]
    public void Taking_control_twice_is_one_handover()
    {
        var (arbiter, _) = Build();
        Assert.NotNull(arbiter.RequestControl("device-a", take: true));
        Assert.Null(arbiter.RequestControl("device-a", take: true));
    }

    [Fact]
    public void Only_the_holder_can_hand_the_display_back()
    {
        var (arbiter, _) = Build();
        arbiter.RequestControl("device-a", take: true);

        Assert.Null(arbiter.RequestControl("device-b", take: false));
        Assert.Equal(DisplayControlHolder.User, arbiter.Holder);

        Assert.Equal(DisplayControlHolder.None, arbiter.RequestControl("device-a", take: false)!.Holder);
    }

    [Fact]
    public void An_untouched_hold_expires_so_the_agent_is_never_locked_out_forever()
    {
        var (arbiter, clock) = Build(new DisplayOptions { ControlIdleSeconds = 60 });
        arbiter.RequestControl("device-a", take: true);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(arbiter.ReleaseIfIdle());
        Assert.False(arbiter.AgentMayInject);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(DisplayControlHolder.None, arbiter.ReleaseIfIdle()!.Holder);
        Assert.True(arbiter.AgentMayInject);
    }

    [Fact]
    public void Activity_keeps_a_hold_alive()
    {
        var (arbiter, clock) = Build(new DisplayOptions { ControlIdleSeconds = 60 });
        arbiter.RequestControl("device-a", take: true);

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            arbiter.NoteUserActivity();
        }

        Assert.Null(arbiter.ReleaseIfIdle());
        Assert.Equal(DisplayControlHolder.User, arbiter.Holder);
    }

    [Fact]
    public void A_gone_device_releases_its_hold_and_nobody_elses()
    {
        var (arbiter, _) = Build();
        arbiter.RequestControl("device-a", take: true);

        Assert.Null(arbiter.ReleaseFor("device-b"));
        Assert.Equal(DisplayControlHolder.User, arbiter.Holder);

        Assert.Equal(DisplayControlHolder.None, arbiter.ReleaseFor("device-a")!.Holder);
    }

    // ---- budget ----

    [Fact]
    public void The_per_minute_budget_is_spent_and_then_refilled_by_time()
    {
        var (arbiter, clock) = Build(new DisplayOptions { InputEventsPerMinute = 10 });

        arbiter.SpendBudget(10);
        var exhausted = Assert.Throws<InvalidOperationException>(() => arbiter.SpendBudget(1));
        Assert.Contains("10 events a minute", exhausted.Message, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(61));
        arbiter.SpendBudget(10); // the window rolled; no exception.
    }

    [Fact]
    public void A_single_tool_call_cannot_exceed_the_per_call_ceiling()
    {
        var (arbiter, _) = Build(new DisplayOptions { InputEventsPerToolCall = 8 });

        arbiter.CheckAgentCall(8);
        var refused = Assert.Throws<InvalidOperationException>(() => arbiter.CheckAgentCall(9));
        Assert.Contains("the limit is 8", refused.Message, StringComparison.Ordinal);
    }

    // ---- blocked chords ----

    [Theory]
    [InlineData("super")]
    [InlineData("super+l")]
    [InlineData("ctrl+alt+F2")]
    [InlineData("alt+ctrl+Delete")]  // written in the other order — the same chord, the same answer.
    [InlineData("ctrl+shift+i")]
    [InlineData("alt+F2")]
    public void The_agent_cannot_press_a_blocked_chord(string chord)
    {
        var (arbiter, _) = Build();
        var refused = Assert.Throws<InvalidOperationException>(() => arbiter.CheckAgentChord(KeyChord.Parse(chord)));
        Assert.Contains("blocked on this host", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ctrl+shift+t")]
    [InlineData("Return")]
    [InlineData("ctrl+a")]
    [InlineData("alt+F4")]
    public void Ordinary_chords_are_allowed(string chord)
    {
        var (arbiter, _) = Build();
        arbiter.CheckAgentChord(KeyChord.Parse(chord));
    }

    [Fact]
    public void The_blocked_list_is_the_operators_to_change()
    {
        var (arbiter, _) = Build(new DisplayOptions { BlockedChords = ["ctrl+q"] });

        arbiter.CheckAgentChord(KeyChord.Parse("super"));   // no longer blocked
        Assert.Throws<InvalidOperationException>(() => arbiter.CheckAgentChord(KeyChord.Parse("ctrl+q")));
    }
}
