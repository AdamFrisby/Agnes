using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The dashboard's derivations, against the state this host is actually in: ten queued items, every one
/// of them dependency-blocked, nothing running, and a paused queue.
/// </summary>
public sealed class DashboardTests
{
    private static WorkItemRow Item(string state, bool depsSatisfied = true, int priority = 0, long pos = 0)
        => new(
            Id: Guid.NewGuid().ToString("N"),
            Title: "t",
            State: state,
            Agent: "claude",
            ProjectId: "codeybox-self",
            QueuePosition: pos,
            UpdatedAt: DateTimeOffset.UtcNow,
            LastError: null,
            DependsOnSatisfied: depsSatisfied,
            Priority: priority);

    /// <summary>The live shape: 10 queued, all blocked.</summary>
    private static readonly WorkItemRow[] Stalled =
        [.. Enumerable.Range(0, 10).Select(i => Item("Queued", depsSatisfied: false, priority: i))];

    [Fact]
    public void Runnable_excludes_queued_items_that_are_waiting_on_something()
    {
        // The number the 404-row list could not show. "Queued: 10" here is not merely unhelpful, it is
        // misleading — resuming the queue would start nothing.
        Assert.Equal(10, Dashboard.Queued(Stalled));
        Assert.Equal(0, Dashboard.Runnable(Stalled));
    }

    [Fact]
    public void The_runnable_tile_says_why_it_is_zero()
    {
        var tile = Dashboard.Tiles(Stalled, queuePaused: false, slotsInUse: 0, slotsTotal: 3)
            .Single(t => t.Label == "Runnable now");

        Assert.Equal("0", tile.Value);
        Assert.Equal("all 10 queued items are waiting on dependencies", tile.Caption);
        Assert.True(tile.IsBad);
    }

    [Fact]
    public void An_empty_queue_is_not_reported_as_a_problem()
    {
        // Zero runnable because there is no work is a completely different state from zero runnable
        // because everything is blocked, and must not wear the same colour.
        var tile = Dashboard.Tiles([Item("Done")], queuePaused: false, slotsInUse: 0, slotsTotal: 3)
            .Single(t => t.Label == "Runnable now");

        Assert.Equal("nothing is queued", tile.Caption);
        Assert.False(tile.IsBad);
    }

    [Fact]
    public void Stalled_means_running_but_unable_to_start_anything()
    {
        Assert.True(Dashboard.IsStalled(Stalled, queuePaused: false));

        // Paused is a decision someone made, not a stall.
        Assert.False(Dashboard.IsStalled(Stalled, queuePaused: true));

        // Idle is not a stall either: there is simply nothing to do.
        Assert.False(Dashboard.IsStalled([Item("Done")], queuePaused: false));

        // Nor is a queue that is working through eligible items.
        Assert.False(Dashboard.IsStalled([Item("Queued"), Item("Working")], queuePaused: false));
    }

    [Fact]
    public void A_paused_queue_leads_with_that_rather_than_with_counts()
    {
        var tile = Dashboard.Tiles(Stalled, queuePaused: true, slotsInUse: 0, slotsTotal: 3)
            .Single(t => t.Label == "Queue");

        Assert.Equal("Paused", tile.Value);
        Assert.True(tile.IsBad);
        Assert.Contains("resumed", tile.Caption);
    }

    [Fact]
    public void Failed_and_blocked_ask_for_a_person_rather_than_reporting_a_fault()
    {
        var items = new[] { Item("Failed"), Item("Queued", depsSatisfied: false), Item("Done") };
        var tiles = Dashboard.Tiles(items, queuePaused: false, slotsInUse: 0, slotsTotal: 3);

        Assert.True(tiles.Single(t => t.Label == "Failed").IsAttention);
        Assert.True(tiles.Single(t => t.Label == "Blocked").IsAttention);
    }

    [Fact]
    public void Nothing_wrong_wears_no_colour_at_all()
    {
        var tiles = Dashboard.Tiles([Item("Done"), Item("Done")], queuePaused: false, slotsInUse: 0, slotsTotal: 3);

        Assert.All(tiles, t => Assert.False(t.IsBad || t.IsAttention));
    }

    [Fact]
    public void Next_up_is_priority_order_and_keeps_blocked_items_visible()
    {
        var items = new[]
        {
            Item("Queued", depsSatisfied: false, priority: 19, pos: 5),
            Item("Queued", depsSatisfied: true, priority: 14, pos: 1),
            Item("Done", priority: 99),
        };

        var next = Dashboard.NextUp(items);

        Assert.Equal(2, next.Count);                 // the finished item is not "next"
        Assert.Equal(19, next[0].Priority);          // priority beats queue position
        Assert.False(next[0].DependsOnSatisfied);    // and a blocked head-of-queue is still shown
    }

    [Fact]
    public void A_perfect_health_score_over_zero_transitions_is_not_reported_as_healthy()
    {
        // The orchestrator scores an empty window as 1.0. This host reports exactly that, because nothing
        // has run — rendering it as "100% healthy" would turn silence into a green tick.
        Assert.False(Dashboard.HealthIsMeaningful(0));
        Assert.Equal("no transitions in the last day — nothing to measure", Dashboard.HealthLabel(1.0, 0));

        Assert.True(Dashboard.HealthIsMeaningful(40));
        Assert.Equal("95% clean over 40 transitions", Dashboard.HealthLabel(0.95, 40));
    }
}
