using Agnes.Host.Sessions;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

/// <summary>
/// The idle rule and the bounds around it. Idle-triggered nudging is only safe if it never talks over a
/// working agent and can never run forever, so both are pinned here rather than left to a live run.
/// </summary>
public sealed class SessionGoalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static SessionGoal Armed(int idleSeconds = 60, int maxProds = 3, int used = 0, DateTimeOffset? expires = null)
        => new("g1", "s1", "finish the migration", idleSeconds, maxProds, used, Armed: true, CreatedAt: T0, ExpiresAt: expires);

    [Fact]
    public void A_working_session_is_never_nudged_however_long_it_has_run()
    {
        // The whole point of idle-triggering: a long turn is work, not a stall.
        var decision = SessionGoalManager.Decide(Armed(), SessionRunState.Working, T0, T0.AddHours(3));

        Assert.Equal(GoalDecision.Nothing, decision);
    }

    [Fact]
    public void An_idle_session_inside_the_window_is_left_alone()
        => Assert.Equal(
            GoalDecision.Nothing,
            SessionGoalManager.Decide(Armed(idleSeconds: 60), SessionRunState.Idle, T0, T0.AddSeconds(59)));

    [Fact]
    public void An_idle_session_past_the_window_is_nudged()
        => Assert.Equal(
            GoalDecision.Prod,
            SessionGoalManager.Decide(Armed(idleSeconds: 60), SessionRunState.Idle, T0, T0.AddSeconds(60)));

    [Fact]
    public void A_dormant_session_still_counts_as_idle()
    {
        // Dormant is the strongest form of stopped — prompting it resurrects the agent, which is the point.
        var decision = SessionGoalManager.Decide(Armed(idleSeconds: 60), SessionRunState.Dormant, T0, T0.AddMinutes(30));

        Assert.Equal(GoalDecision.Prod, decision);
    }

    [Fact]
    public void A_disarmed_goal_does_nothing_even_when_idle()
        => Assert.Equal(
            GoalDecision.Nothing,
            SessionGoalManager.Decide(Armed() with { Armed = false }, SessionRunState.Idle, T0, T0.AddHours(1)));

    [Fact]
    public void The_budget_is_reported_before_the_idle_check()
    {
        // Exhausted must win over Prod, or a spent goal would nudge once more on its way out.
        var decision = SessionGoalManager.Decide(Armed(maxProds: 3, used: 3), SessionRunState.Idle, T0, T0.AddHours(1));

        Assert.Equal(GoalDecision.Exhausted, decision);
    }

    [Fact]
    public void Expiry_wins_over_everything()
    {
        var goal = Armed(maxProds: 10, used: 0, expires: T0.AddMinutes(5));

        Assert.Equal(GoalDecision.Expired, SessionGoalManager.Decide(goal, SessionRunState.Idle, T0, T0.AddMinutes(5)));
        // …and an expired goal is not nudged even though it is otherwise due.
        Assert.NotEqual(GoalDecision.Prod, SessionGoalManager.Decide(goal, SessionRunState.Idle, T0, T0.AddHours(1)));
    }

    // ---- bounds enforced at arm time ----

    [Fact]
    public void An_implausibly_short_idle_window_is_raised_to_the_floor()
    {
        // Nudging after a few seconds would interrupt an agent merely pausing between tool calls.
        var goal = new SessionGoalManager().Arm(new ArmGoalRequest("s1", "go", IdleSeconds: 1), T0);

        Assert.Equal(SessionGoalManager.MinimumIdleSeconds, goal.IdleSeconds);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(10_000, SessionGoalManager.MaximumProds)]
    public void A_finite_nudge_budget_is_clamped_to_the_ceiling(int requested, int expected)
    {
        // Non-positive values are NOT clamped up — they mean "unlimited"; see the unlimited tests below.
        var goal = new SessionGoalManager().Arm(new ArmGoalRequest("s1", "go", 60, MaxProds: requested), T0);

        Assert.Equal(expected, goal.MaxProds);
    }

    [Fact]
    public void Arming_a_second_goal_supersedes_the_first_rather_than_stacking()
    {
        // Two armed goals would give a stalled agent two different instructions and no way to choose.
        var manager = new SessionGoalManager();
        var first = manager.Arm(new ArmGoalRequest("s1", "first", 60), T0);
        manager.Arm(new ArmGoalRequest("s1", "second", 60), T0);

        var stored = manager.Get(first.Id);
        Assert.NotNull(stored);
        Assert.False(stored!.Armed);
        Assert.Equal("superseded", stored.DisarmedReason);
        Assert.Single(manager.ListFor("s1"), g => g.Armed);
    }

    [Fact]
    public void A_goal_on_another_session_is_untouched_by_arming()
    {
        var manager = new SessionGoalManager();
        var other = manager.Arm(new ArmGoalRequest("s2", "other", 60), T0);
        manager.Arm(new ArmGoalRequest("s1", "mine", 60), T0);

        Assert.True(manager.Get(other.Id)!.Armed);
    }

    [Fact]
    public void Disarming_keeps_the_first_reason_rather_than_overwriting_it()
    {
        // "completed" must not later read as "cancelled" because something tidied up afterwards.
        var manager = new SessionGoalManager();
        var goal = manager.Arm(new ArmGoalRequest("s1", "go", 60), T0);

        manager.Disarm(goal.Id, "completed");
        var after = manager.Disarm(goal.Id, "cancelled");

        Assert.Equal("completed", after!.DisarmedReason);
    }

    [Fact]
    public void Recording_a_nudge_advances_the_budget_and_stops_at_disarm()
    {
        var manager = new SessionGoalManager();
        var goal = manager.Arm(new ArmGoalRequest("s1", "go", 60, MaxProds: 2), T0);

        Assert.Equal(1, manager.RecordProd(goal.Id, T0)!.ProdsUsed);
        Assert.Equal(2, manager.RecordProd(goal.Id, T0)!.ProdsUsed);

        manager.Disarm(goal.Id, "completed");
        Assert.Null(manager.RecordProd(goal.Id, T0)); // a disarmed goal can't be nudged
    }

    [Fact]
    public void Goals_survive_a_restart()
    {
        // Durability is the reason to arm a goal host-side instead of looping in the conversation.
        var file = Path.Combine(Path.GetTempPath(), $"agnes-goals-{Guid.NewGuid():n}.json");
        try
        {
            var armed = new SessionGoalManager(file).Arm(new ArmGoalRequest("s1", "survive", 90, MaxProds: 4), T0);

            var reloaded = new SessionGoalManager(file).Get(armed.Id);

            Assert.NotNull(reloaded);
            Assert.True(reloaded!.Armed);
            Assert.Equal("survive", reloaded.Goal);
            Assert.Equal(90, reloaded.IdleSeconds);
            Assert.Equal(4, reloaded.MaxProds);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void A_corrupt_goals_file_does_not_stop_the_host_starting()
    {
        var file = Path.Combine(Path.GetTempPath(), $"agnes-goals-{Guid.NewGuid():n}.json");
        try
        {
            File.WriteAllText(file, "this is not json");

            Assert.Empty(new SessionGoalManager(file).List()); // constructed fine, just empty
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    // ---- unlimited budgets ----

    [Fact]
    public void An_unlimited_goal_is_never_exhausted_however_many_nudges_it_has_used()
    {
        // On an agent that stalls often, any finite budget runs out long before the work is done.
        var goal = Armed(maxProds: SessionGoalManager.Unlimited, used: 9_999);

        Assert.Equal(GoalDecision.Prod, SessionGoalManager.Decide(goal, SessionRunState.Idle, T0, T0.AddHours(1)));
    }

    [Fact]
    public void An_unlimited_goal_still_expires()
    {
        // Unlimited means "no nudge ceiling", not "runs forever" — expiry and disarming still stop it.
        var goal = Armed(maxProds: SessionGoalManager.Unlimited, expires: T0.AddMinutes(5));

        Assert.Equal(GoalDecision.Expired, SessionGoalManager.Decide(goal, SessionRunState.Idle, T0, T0.AddMinutes(6)));
    }

    [Fact]
    public void An_unlimited_goal_still_leaves_a_working_session_alone()
        => Assert.Equal(
            GoalDecision.Nothing,
            SessionGoalManager.Decide(
                Armed(maxProds: SessionGoalManager.Unlimited), SessionRunState.Working, T0, T0.AddHours(4)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_budget_is_preserved_as_unlimited_rather_than_clamped_up(int requested)
    {
        var goal = new SessionGoalManager().Arm(new ArmGoalRequest("s1", "go", 60, MaxProds: requested), T0);

        Assert.Equal(SessionGoalManager.Unlimited, goal.MaxProds);
    }

}
