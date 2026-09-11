using System.Text.Json;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The sampler records every model an agent can route to, sometimes all under a null model id, so one
/// (agent, window) key can hold two interleaved series. The live instance drew antigravity's seven-day
/// window at 100% while the router had it at 0.4%: the sibling model's row came last each tick.
/// </summary>
public class QuotaStrandTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private static QuotaHistoryRow Row(int tick, double pct, string? model = null, string window = "seven_day")
        => new(T0.AddMinutes(15 * tick), "antigravity", model, null, false, null, window, pct, T0.AddHours(8), true, null);

    private static QuotaProbe Probe(string agent, double? sevenDay)
    {
        var pct = (sevenDay ?? 50).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var json = "{\"agent\":\"" + agent + "\",\"latestSnapshot\":{\"availablePct\":" + pct
            + ",\"isKnown\":true,\"resetAt\":\"2026-09-11T18:00:00Z\",\"windows\":[{\"name\":\"seven_day\",\"availablePct\":"
            + pct + ",\"resetAt\":\"2026-09-11T18:00:00Z\"}]}}";
        return JsonSerializer.Deserialize<QuotaProbe>(json)!;
    }

    [Fact]
    public void Two_models_under_one_window_are_dealt_into_strands_and_the_probe_picks_the_routed_one()
    {
        // Each tick: the routed model first (burning down), then a sibling that is always full.
        List<QuotaHistoryRow> rows = [];
        for (var tick = 0; tick < 4; tick++)
        {
            rows.Add(Row(tick, 40 - (tick * 10)));
            rows.Add(Row(tick, 100));
        }

        var burn = Assert.Single(QuotaHistoryMap.ToBurnDown(rows, [Probe("antigravity", 0.4)], T0.AddHours(2)));
        // The probe reads 0.4 now: the strand ending at 10 is the routed model, and the probe's reading
        // is appended as the newest point so the chart ends where the router is.
        Assert.Equal([40, 30, 20, 10, 0.4], burn.Samples.Select(s => s.Pct));
        Assert.Equal(0.4, burn.NowPct);
    }

    [Fact]
    public void With_nothing_to_steer_by_the_first_strand_is_the_series()
    {
        List<QuotaHistoryRow> rows = [Row(0, 40), Row(0, 100), Row(1, 30), Row(1, 100)];

        var burn = Assert.Single(QuotaHistoryMap.ToBurnDown(rows));
        Assert.Equal([40, 30], burn.Samples.Select(s => s.Pct));
    }

    [Fact]
    public void Rows_naming_a_model_yield_to_the_unnamed_routed_rows()
    {
        List<QuotaHistoryRow> rows = [Row(0, 0), Row(0, 100, model: "GPT-5.3-Codex-Spark"), Row(1, 0), Row(1, 100, model: "GPT-5.3-Codex-Spark")];

        var burn = Assert.Single(QuotaHistoryMap.ToBurnDown(rows));
        Assert.Equal([0, 0], burn.Samples.Select(s => s.Pct));
    }

    [Fact]
    public void A_single_series_is_left_exactly_as_it_was()
    {
        List<QuotaHistoryRow> rows = [Row(0, 90), Row(1, 80), Row(2, 70)];

        var burn = Assert.Single(QuotaHistoryMap.ToBurnDown(rows, [Probe("antigravity", 70)], T0.AddMinutes(30)));
        // The probe agrees with the last sample within the lag, so nothing is appended.
        Assert.Equal([90, 80, 70], burn.Samples.Select(s => s.Pct));
    }
}
