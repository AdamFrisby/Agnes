using Agnes.Host.Sessions;

namespace Agnes.Host.Tests;

/// <summary>
/// Telling a working agent from a wedged one. The rule has to survive the case that actually happened: a
/// session emitted nothing for 1h38m while a subagent worked perfectly, so "quiet" alone cannot mean stuck.
/// </summary>
public sealed class SessionLivenessTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    [Fact]
    public void A_long_running_tool_call_is_never_called_wedged()
    {
        // The real case: a `task` tool sat in flight for 1h38m while its subagent ran, and not one parent
        // event crossed the ACP boundary in all that time.
        var activity = new SessionActivity(TurnActive: true, ToolCallsInFlight: 1, Quiet: TimeSpan.FromHours(2));

        Assert.Equal(LivenessVerdict.Fine, SessionLiveness.Assess(activity, Limit));
    }

    [Fact]
    public void Silence_with_nothing_outstanding_is_wedged()
    {
        var activity = new SessionActivity(TurnActive: true, ToolCallsInFlight: 0, Quiet: TimeSpan.FromMinutes(11));

        Assert.Equal(LivenessVerdict.Wedged, SessionLiveness.Assess(activity, Limit));
    }

    [Fact]
    public void Silence_inside_the_limit_is_left_alone()
    {
        // A throttled model can go minutes between tokens; crying wolf here teaches the operator to ignore it.
        var activity = new SessionActivity(TurnActive: true, ToolCallsInFlight: 0, Quiet: TimeSpan.FromMinutes(9));

        Assert.Equal(LivenessVerdict.Fine, SessionLiveness.Assess(activity, Limit));
    }

    [Fact]
    public void An_idle_session_is_not_this_watchdogs_business()
    {
        // No turn in flight means nothing was promised — that is the goal watcher's concern, not liveness.
        var activity = new SessionActivity(TurnActive: false, ToolCallsInFlight: 0, Quiet: TimeSpan.FromDays(1));

        Assert.Equal(LivenessVerdict.Fine, SessionLiveness.Assess(activity, Limit));
    }

    [Fact]
    public void Exactly_at_the_limit_counts()
        => Assert.Equal(
            LivenessVerdict.Wedged,
            SessionLiveness.Assess(new SessionActivity(true, 0, Limit), Limit));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Any_outstanding_tool_call_suppresses_the_verdict(int inFlight)
        => Assert.Equal(
            LivenessVerdict.Fine,
            SessionLiveness.Assess(new SessionActivity(true, inFlight, TimeSpan.FromHours(6)), Limit));

    [Fact]
    public void The_default_limit_is_generous_enough_not_to_cry_wolf()
    {
        // Ten minutes of total silence mid-turn is well past any normal inter-token gap, including the
        // ~10s-per-chunk cadence seen on a throttled provider.
        Assert.True(SessionLiveness.DefaultQuietLimit >= TimeSpan.FromMinutes(5));
        Assert.True(new LivenessOptions().Enabled);
    }
}
