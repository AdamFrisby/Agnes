namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Every chart on the overview states its scale in words: what period a sparkline covers, when a quota
/// window resets, what a burn-down projects, and what the flow chart's bands add up to. A chart with no
/// stated axis is a shape, and the operator said so.
/// </summary>
public class OverviewLabelsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.FromHours(10));

    [Fact]
    public void A_reset_is_named_as_a_clock_time_and_a_distance()
    {
        Assert.Null(OverviewModel.ResetLabelFor(Now, null));
        // Local clock: these times are built with the machine's offset so the day words hold anywhere.
        var now = new DateTimeOffset(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Local));
        Assert.Equal("resets today 14:30 · in 2h 30m", OverviewModel.ResetLabelFor(now, now.AddHours(2.5)));
        Assert.Equal("resets tomorrow 09:00 · in 21h", OverviewModel.ResetLabelFor(now, now.AddHours(21)));
        Assert.Equal("resets Fri 06:54 · in 5d 18h", OverviewModel.ResetLabelFor(now, now.AddDays(5).AddHours(18).AddMinutes(54)));
        Assert.Equal("resets 26 Sep 03:20 · in 13d 15h", OverviewModel.ResetLabelFor(now, now.AddDays(13).AddHours(15).AddMinutes(20)));
        Assert.Equal("reset was due today 11:00", OverviewModel.ResetLabelFor(now, now.AddHours(-1)));
    }

    [Fact]
    public void A_burn_down_only_projects_when_the_line_is_going_down()
    {
        static QuotaBurn Burn(double? now, double? projected)
            => new("claude", "five_hour", [new BurnSample(Now.AddHours(-1), 100), new BurnSample(Now, now ?? 100)], Now.AddHours(1), now, projected, Eligible: true);

        Assert.True(Burn(55, 30).IsBurning);
        Assert.Equal("leaves ~30% unspent at reset", Burn(55, 30).ProjectionLabel);
        // Flat at 100 with a projection of 100: an agent nobody dispatched to, not a broken chart.
        Assert.False(Burn(100, 100).IsBurning);
        Assert.Equal("not burning", Burn(100, 100).ProjectionLabel);
        // No projection but a flat line says the same thing; a line that moved with no fit says nothing.
        Assert.Equal("not burning", Burn(100, null).ProjectionLabel);
        Assert.Null(Burn(60, null).ProjectionLabel);
    }

    [Fact]
    public void The_flow_legend_counts_only_what_happened_inside_the_window()
    {
        var series = new FlowSeries(
        [
            new FlowPoint(new DateOnly(2026, 8, 13), Created: 400, Landed: 322, Cancelled: 50),
            new FlowPoint(new DateOnly(2026, 9, 11), Created: 455, Landed: 354, Cancelled: 54),
        ]);

        Assert.Equal(322, series.Floor);
        Assert.Equal("+32 landed", series.LandedLegend);
        Assert.Equal("47 in flight", series.InFlightLegend);
        Assert.Equal("+4 cancelled", series.CancelledLegend);
        Assert.True(series.HasCancelled);
        // The stack's top as drawn: landed + in flight + cancelled inside the window.
        Assert.Equal(354 + 47 + 4, series.Peak);
    }

    [Fact]
    public void The_overview_says_its_clocks_are_local_and_how_fresh_it_is()
    {
        var overview = OverviewModel.Build(new OverviewInputs(
            Now, [], [], new Dictionary<string, int>(), null, null, [], [], null, [], new Dictionary<string, int>()));

        Assert.Equal(
            $"as of {Now.ToLocalTime():HH:mm} · times are local",
            overview.AsOf);
    }
}
