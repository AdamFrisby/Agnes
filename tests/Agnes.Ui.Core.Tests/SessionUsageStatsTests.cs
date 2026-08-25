using Agnes.Abstractions;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// Session token totals. The trap this guards is the difference between a level and a flow: context
/// occupancy is the window's state and adding it up means nothing, while the per-kind figures here are
/// what each model call consumed and only mean something added up.
/// </summary>
public class SessionUsageStatsTests
{
    private static DateTimeOffset Now => new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static SessionUsageStats Stats() => new(() => Now);

    private static UsageMetrics Report(long input = 0, long cacheRead = 0, long cacheWrite = 0, long output = 0)
        => new(ContextUsed: 50_000, ContextWindow: 200_000, OutputTokens: output,
               InputTokens: input, CacheReadTokens: cacheRead, CacheWriteTokens: cacheWrite);

    [Fact]
    public void Consumption_accumulates_where_occupancy_would_not()
    {
        var stats = Stats();
        stats.Add(Report(input: 1_000, output: 500), Now);
        stats.Add(Report(input: 2_000, output: 700), Now);

        var input = stats.Rows.Single(r => r.Kind == TokenKind.Input);
        var output = stats.Rows.Single(r => r.Kind == TokenKind.Output);
        Assert.Equal(3_000, input.Lifetime);
        Assert.Equal(1_200, output.Lifetime);

        // Both reports also carried ContextUsed 50,000. Adding *that* up would claim 100,000 tokens of a
        // 200,000 window were occupied by two calls that never exceeded half of it.
        Assert.Equal(4_200, stats.TotalLifetime);
    }

    [Fact]
    public void Each_window_counts_only_what_falls_inside_it()
    {
        var stats = Stats();
        stats.Add(Report(input: 100), Now.AddHours(-2));    // today
        stats.Add(Report(input: 30), Now.AddDays(-3));      // this week, not today
        stats.Add(Report(input: 7), Now.AddDays(-30));      // lifetime only

        var input = stats.Rows.Single(r => r.Kind == TokenKind.Input);
        Assert.Equal(100, input.Day);
        Assert.Equal(130, input.Week);
        Assert.Equal(137, input.Lifetime);
    }

    [Fact]
    public void Aged_out_detail_is_dropped_but_never_the_lifetime_total()
    {
        var stats = Stats();
        stats.Add(Report(input: 5_000), Now.AddDays(-90));

        // The bucket is long gone — a session running for months must not carry a bucket per hour of it —
        // yet what it consumed is still counted, because lifetime is kept as a running total.
        var input = stats.Rows.Single(r => r.Kind == TokenKind.Input);
        Assert.Equal(0, input.Week);
        Assert.Equal(5_000, input.Lifetime);
    }

    [Fact]
    public void An_agent_that_reports_no_breakdown_produces_no_panel()
    {
        var stats = Stats();
        // What ACP's usage_update carries: occupancy and cost, no token kinds at all.
        stats.Add(new UsageMetrics(ContextUsed: 12_000, ContextWindow: 200_000, CostUsd: 0.02), Now);

        // Showing a table of zeroes here would read as "this session consumed nothing", which is a
        // claim we have no evidence for — the agent simply never said.
        Assert.False(stats.HasBreakdown);
        Assert.Equal(0, stats.TotalLifetime);
    }

    [Fact]
    public void Figures_are_abbreviated_to_fit_a_sidebar_column()
    {
        Assert.Equal("812", UsageStatRow.Format(812));
        Assert.Equal("1.2k", UsageStatRow.Format(1_234));
        Assert.Equal("943k", UsageStatRow.Format(943_210));
        Assert.Equal("4.6M", UsageStatRow.Format(4_600_000));
    }

    [Fact]
    public void Rows_stay_in_a_stable_order_as_they_update()
    {
        var stats = Stats();
        stats.Add(Report(input: 1), Now);
        Assert.Equal(
            [TokenKind.Input, TokenKind.CacheRead, TokenKind.CacheWrite, TokenKind.Output],
            stats.Rows.Select(r => r.Kind));

        // Updating replaces rows in place rather than rebuilding the list, so a bound panel doesn't
        // flicker through an empty state on every model call.
        stats.Add(Report(cacheRead: 9), Now);
        Assert.Equal(4, stats.Rows.Count);
        Assert.Equal(9, stats.Rows.Single(r => r.Kind == TokenKind.CacheRead).Lifetime);
    }
}
