namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// One card per agent: the longest window is the chart, the shorter ones are gauges beside it. The
/// operator: "we really only care about the big windows".
/// </summary>
public class QuotaCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    private static QuotaBurn Burn(string agent, string? window, double pct, TimeSpan? resetIn = null, bool eligible = false)
        => new(agent, window, [new BurnSample(Now.AddHours(-2), 100), new BurnSample(Now, pct)],
            resetIn is { } r ? Now + r : null, pct, null, eligible);

    [Fact]
    public void The_longest_window_by_name_is_the_chart_and_the_rest_are_gauges()
    {
        var cards = OverviewModel.Cards(
        [
            Burn("antigravity", "five_hour", 0),
            Burn("antigravity", "seven_day", 83.3),
            Burn("codex", "5h-rolling", 2),
            Burn("codex", "weekly", 0),
        ], Now);

        Assert.Equal(["antigravity", "codex"], cards.Select(c => c.Agent));
        Assert.Equal("seven_day", cards[0].Primary.Window);
        Assert.Equal(["five hour"], cards[0].Others.Select(o => o.WindowShort));
        Assert.Equal("weekly", cards[1].Primary.Window);
        Assert.Equal(["5h rolling"], cards[1].Others.Select(o => o.WindowShort));
    }

    [Fact]
    public void An_unnamed_window_is_ranked_by_how_far_away_its_reset_is()
    {
        var cards = OverviewModel.Cards(
        [
            Burn("opencode", "rolling", 93, resetIn: TimeSpan.FromMinutes(30)),
            Burn("opencode", "cycle", 43, resetIn: TimeSpan.FromDays(6)),
        ], Now);

        Assert.Equal("cycle", Assert.Single(cards).Primary.Window);
    }

    [Fact]
    public void Agents_the_router_would_dispatch_to_come_first()
    {
        var cards = OverviewModel.Cards(
        [
            Burn("antigravity", "seven_day", 0.4),
            Burn("crock", "seven_day", 80, eligible: true),
        ], Now);

        Assert.Equal(["crock", "antigravity"], cards.Select(c => c.Agent));
    }

    [Fact]
    public void A_single_window_agent_has_no_gauges()
    {
        var card = Assert.Single(OverviewModel.Cards([Burn("cursor", null, 0)], Now));
        Assert.False(card.HasOthers);
        Assert.Equal("overall", card.Primary.WindowShort);
    }
}
