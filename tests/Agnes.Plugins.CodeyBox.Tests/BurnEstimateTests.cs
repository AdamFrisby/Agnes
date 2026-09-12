namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The time to drain the queue: what is left times what an item costs in active agent time, less what
/// the in-flight items have already had, spread over the slots. The operator asked for this in those
/// words; the one thing added is the division by slots, because six agents drain a queue six times faster
/// than the arithmetic over one would say.
/// </summary>
public class BurnEstimateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static WorkItemRow Item(string id, string state)
        => new(id, id, state, "copilot", "p", 0, Now.AddHours(-1), null);

    private static ItemEffort Landed(string id, double hours, int daysAgo)
        => new(id, TimeSpan.FromHours(hours), Landed: true, Now.AddDays(-daysAgo));

    private static ItemEffort Live(string id, double hours)
        => new(id, TimeSpan.FromHours(hours), Landed: false, Now);

    private static OverviewInputs Inputs(IReadOnlyList<WorkItemRow> items, IReadOnlyList<ItemEffort> effort, int slots = 1)
        => new(Now, items, [], new Dictionary<string, int>(), null,
            new Concurrency(slots, Math.Min(slots, items.Count(i => i.IsActive)), null), [], [], null, [],
            new Dictionary<string, int>())
        {
            Effort = effort,
        };

    [Fact]
    public void Remaining_items_times_the_median_less_what_is_already_spent_over_the_slots()
    {
        // Five landed items: 2h, 3h, 4h, 5h, 6h → median 4h. Three remain: two queued, one working for 1h.
        var items = new[] { Item("q1", "Queued"), Item("q2", "Queued"), Item("w1", "Working"), Item("d1", "Done") };
        var effort = new[]
        {
            Landed("a", 2, 5), Landed("b", 3, 4), Landed("c", 4, 3), Landed("d", 5, 2), Landed("e", 6, 1),
            Live("w1", 1),
        };

        var burn = OverviewModel.BuildBurn(Inputs(items, effort, slots: 2));

        Assert.NotNull(burn);
        Assert.Equal(3, burn!.Remaining);
        Assert.Equal(5, burn.Sampled);
        Assert.Equal(TimeSpan.FromHours(4), burn.MedianPerItem);
        Assert.Equal(TimeSpan.FromHours(1), burn.SpentOnLive);
        // 3 × 4h − 1h = 11h of agent time; over two slots, 5h 30m of wall clock.
        Assert.Equal(TimeSpan.FromHours(11), burn.WorkRemaining);
        Assert.Equal(2, burn.Slots);
        Assert.Equal(TimeSpan.FromHours(5.5), burn.Wall);
        // The sparkline is the sample, oldest first, in hours.
        Assert.Equal([2, 3, 4, 5, 6], burn.SparkHours);
    }

    [Fact]
    public void Only_the_most_recent_landed_items_form_the_sample()
    {
        var effort = Enumerable.Range(0, 30).Select(i => Landed($"l{i}", i < 10 ? 100 : 2, daysAgo: 30 - i)).ToList();

        var burn = OverviewModel.BuildBurn(Inputs([Item("q", "Queued")], effort));

        Assert.Equal(OverviewModel.BurnSample, burn!.Sampled);
        Assert.Equal(TimeSpan.FromHours(2), burn.MedianPerItem);
    }

    [Fact]
    public void Too_few_landed_items_and_there_is_no_estimate()
    {
        var burn = OverviewModel.BuildBurn(Inputs([Item("q", "Queued")], [Landed("a", 2, 1), Landed("b", 3, 2)]));
        Assert.Null(burn);
    }

    [Fact]
    public void Time_already_spent_never_takes_the_estimate_below_zero()
    {
        var items = new[] { Item("w1", "Working") };
        var effort = new[] { Landed("a", 1, 3), Landed("b", 1, 2), Landed("c", 1, 1), Live("w1", 9) };

        var burn = OverviewModel.BuildBurn(Inputs(items, effort));

        Assert.Equal(TimeSpan.Zero, burn!.WorkRemaining);
        Assert.Equal(TimeSpan.Zero, burn.Wall);
    }

    [Fact]
    public void An_empty_queue_has_nothing_to_drain_and_says_so_without_a_number()
    {
        var overview = OverviewModel.Build(Inputs([Item("d", "Done")], [Landed("a", 1, 3), Landed("b", 1, 2), Landed("c", 1, 1)]));
        var vital = overview.Vitals.Single(v => v.Label == "Time to drain");
        Assert.Equal("nothing queued", vital.Value);
    }

    [Fact]
    public void The_vital_says_its_arithmetic_out_loud()
    {
        var items = new[] { Item("q1", "Queued"), Item("q2", "Queued"), Item("w1", "Working") };
        var effort = new[] { Landed("a", 2, 3), Landed("b", 4, 2), Landed("c", 6, 1), Live("w1", 1) };

        var overview = OverviewModel.Build(Inputs(items, effort, slots: 2));
        var vital = overview.Vitals.Single(v => v.Label == "Time to drain");

        Assert.Equal("~5h 30m", vital.Value);
        Assert.Equal("3 items × 4h median − 1h already spent, over 2 slots", vital.Caption);
        Assert.Equal("last 3 landed", vital.Period);
        Assert.Equal(4, vital.Median);
    }

    [Fact]
    public void Without_effort_data_the_vitals_are_the_five_they_always_were()
    {
        var overview = OverviewModel.Build(Inputs([Item("q", "Queued")], []));
        Assert.Equal(5, overview.Vitals.Count);
        Assert.Null(overview.Burn);
    }

    [Fact]
    public void Active_time_is_the_sum_of_the_runs_with_an_open_run_counted_to_now()
    {
        var runs = new[]
        {
            new AgentRun("r1", "copilot", null, "work", Now.AddHours(-5), Now.AddHours(-4), null, "success"),
            new AgentRun("r2", "copilot", null, "audit:tests", Now.AddHours(-3), Now.AddHours(-2.5), 1, "success"),
            new AgentRun("r3", "copilot", null, "rework", Now.AddMinutes(-30), null, 2, null),
        };

        Assert.Equal(TimeSpan.FromHours(2), ItemEffort.ActiveTime(runs, Now));
    }
}
