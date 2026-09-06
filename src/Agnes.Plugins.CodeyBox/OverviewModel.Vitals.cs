using System.Globalization;

namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// Band 1: five vitals.
//
// Each one is a number, a sparkline of the fleet's own recent readings, and a control band drawn from
// that same history. The band is what makes a number readable: "12 landed" means nothing until you know
// this fleet usually lands 18. Nothing here is judged against an absolute target, and nothing here is a
// cost figure.
//
// A vital only leaves Neutral for a reason a person could act on. In particular "landed this week" never
// goes Bad: a quiet week is quiet, not broken, and colouring it red teaches the operator to ignore red.
// ---------------------------------------------------------------------------------------------------

public static partial class OverviewModel
{
    /// <summary>A vital's control band and where the current reading sits against it.</summary>
    internal readonly record struct Norm(
        IReadOnlyList<double> Spark,
        double? Median,
        double? Low,
        double? High,
        Trend Trend)
    {
        public bool Below(double current) => Low is { } low && current < low;

        public bool Above(double current) => High is { } high && current > high;
    }

    private static IReadOnlyList<Vital> BuildVitals(OverviewInputs inputs, FleetCounts counts)
        =>
        [
            Landed(inputs, counts),
            InMotion(inputs, counts),
            EligibleCapacity(inputs, counts),
            InfraFailures(inputs),
            BlockedOnYou(inputs, counts),
        ];

    // -------------------------------------------------------------------------------------------

    private static Vital Landed(OverviewInputs inputs, FleetCounts counts)
    {
        double current = counts.Landed7d;
        var norm = Normal(inputs, s => s.Landed7d, current);

        return new Vital(
            "Landed this week",
            counts.Landed7d.ToString(CultureInfo.InvariantCulture),
            norm.Median is { } median ? Inv($"vs a usual {median:0}") : "no history yet",

            // Never Bad. A week below the usual rate is worth noticing and is not a failure, and this is
            // the one vital an operator would otherwise learn to read as an accusation.
            norm.Below(current) ? TileTone.Attention : TileTone.Neutral,
            norm.Spark,
            norm.Median,
            norm.Low,
            norm.High,
            current,
            norm.Trend);
    }

    private static Vital InMotion(OverviewInputs inputs, FleetCounts counts)
    {
        var stopped = counts.Wedged + counts.Parked + counts.Blocked;
        double current = counts.Moving;
        var norm = Normal(inputs, s => s.InMotion, current);

        List<string> parts = [];
        if (counts.Wedged > 0)
        {
            parts.Add(Inv($"{counts.Wedged} wedged"));
        }

        if (counts.Parked > 0)
        {
            parts.Add(Inv($"{counts.Parked} parked"));
        }

        if (counts.Blocked > 0)
        {
            parts.Add(Inv($"{counts.Blocked} blocked"));
        }

        var tone = counts.Wedged > 0 ? TileTone.Bad
            : counts.Blocked > 0 ? TileTone.Attention
            : counts.Moving > 0 ? TileTone.Active
            : TileTone.Neutral;

        return new Vital(
            "In motion",
            Inv($"{counts.Moving} moving · {stopped} stopped"),
            parts.Count == 0 ? "nothing stopped" : string.Join(" · ", parts),
            tone,
            norm.Spark,
            norm.Median,
            norm.Low,
            norm.High,
            current,
            norm.Trend);
    }

    private static Vital EligibleCapacity(OverviewInputs inputs, FleetCounts counts)
    {
        double current = counts.EligibleAgents;
        var norm = Normal(inputs, s => s.EligibleAgents, current);

        var value = Inv($"{counts.EligibleAgents} of {counts.TotalAgents} agents");
        if (inputs.Concurrency is { } concurrency)
        {
            value += Inv($" · {concurrency.CurrentlyRunningTotal}/{concurrency.GlobalMaxConcurrent} slots");
        }

        var saturated = inputs.Concurrency?.IsSaturated ?? false;
        var tone = counts.EligibleAgents == 0 && counts.Runnable > 0 ? TileTone.Bad
            : counts.EligibleAgents <= 1 && saturated ? TileTone.Attention
            : counts.Running > 0 ? TileTone.Active
            : TileTone.Neutral;

        return new Vital(
            "Eligible capacity",
            value,
            CapacityCaption(inputs, counts),
            tone,
            norm.Spark,
            norm.Median,
            norm.Low,
            norm.High,
            current,
            norm.Trend);
    }

    /// <summary>
    /// When an agent is gated, the one useful fact is when it comes back — and it has to come from a
    /// probe that is actually gated, since an eligible agent's reset time says nothing about capacity.
    /// </summary>
    private static string CapacityCaption(OverviewInputs inputs, FleetCounts counts)
    {
        var eligible = EligibleAgents(inputs.Probes);

        var next = inputs.Probes
            .Where(p => p.IsKnown
                && !eligible.Contains(p.Agent, StringComparer.OrdinalIgnoreCase)
                && p.LatestSnapshot!.ResetAt > inputs.Now)
            .OrderBy(p => p.LatestSnapshot!.ResetAt!.Value)
            .FirstOrDefault();

        if (next is not null)
        {
            return Inv($"next reset {Clock(next.LatestSnapshot!.ResetAt!.Value)} ({next.Agent})");
        }

        if (inputs.Probes.Count == 0)
        {
            return "no probes";
        }

        return counts.EligibleAgents >= counts.TotalAgents
            ? "all agents eligible"
            : "no reset time known";
    }

    private static Vital InfraFailures(OverviewInputs inputs)
    {
        var health = inputs.Health;
        var rate = health?.InfraFailureRate ?? 0;
        var transitions = health?.TotalTransitions ?? 0;
        var current = rate * 100;
        var norm = Normal(inputs, s => s.InfraFailureRate * 100, current);

        // The orchestrator scores an empty window as a perfect 1.0. Rendering that as a percentage would
        // turn silence into a clean bill of health, so an unmeasured window says so instead.
        if (transitions == 0)
        {
            return new Vital(
                "Infra failures",
                "—",
                "nothing to measure",
                TileTone.Neutral,
                norm.Spark,
                norm.Median,
                norm.Low,
                norm.High,
                current,
                norm.Trend);
        }

        var tone = norm.Above(current) || (rate > InfraBadRate && transitions >= InfraBadMinTransitions)
            ? TileTone.Bad
            : rate > InfraAttentionRate ? TileTone.Attention : TileTone.Neutral;

        var caption = health!.WorstStage is { Length: > 0 } worst
            ? Inv($"over {transitions} {Plural(transitions, "transition", "transitions")}, worst: {worst}")
            : Inv($"over {transitions} {Plural(transitions, "transition", "transitions")}");

        return new Vital(
            "Infra failures",
            Inv($"{current:0}%"),
            caption,
            tone,
            norm.Spark,
            norm.Median,
            norm.Low,
            norm.High,
            current,
            norm.Trend);
    }

    private static Vital BlockedOnYou(OverviewInputs inputs, FleetCounts counts)
    {
        double current = counts.NeedsPerson;
        var norm = Normal(inputs, s => s.BlockedOnYou, current);

        return new Vital(
            "Blocked on you",
            counts.NeedsPerson.ToString(CultureInfo.InvariantCulture),
            counts.NeedsPerson > 0 ? "questions and failed items awaiting a decision" : "nothing needs you",
            counts.NeedsPerson > 0 ? TileTone.Attention : TileTone.Neutral,
            norm.Spark,
            norm.Median,
            norm.Low,
            norm.High,
            current,
            norm.Trend);
    }

    // -------------------------------------------------------------------------------------------
    // The band
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The fleet's own norm for one vital: a sparkline of recent readings, the middle 60% of the settled
    /// ones as a band, and which side of the median today sits on.
    ///
    /// <para>Only samples older than an hour build the band. Refreshes are frequent, so without that the
    /// last few minutes of the current reading would dominate the band it is judged against and every
    /// change would look normal.</para>
    /// </summary>
    internal static Norm Normal(OverviewInputs inputs, Func<OverviewSample, double> pick, double current)
    {
        List<double> spark = [.. inputs.History.Skip(Math.Max(0, inputs.History.Count - SparkWindow)).Select(pick), current];

        List<double> settled =
        [
            .. inputs.History
                .Where(s => inputs.Now - s.At > BandSettleAge)
                .Select(pick)
                .Order()
        ];

        if (settled.Count < BandMinSamples)
        {
            return new Norm(spark, null, null, null, Trend.Unknown);
        }

        var median = MedianOf(settled);
        var trend = current > median * (1 + TrendDeadband) ? Trend.Up
            : current < median * (1 - TrendDeadband) ? Trend.Down
            : Trend.Flat;

        // A median of zero has no proportional band around it, so any non-zero reading is a direction.
        if (median == 0)
        {
            trend = current > 0 ? Trend.Up : Trend.Flat;
        }

        return new Norm(spark, median, Percentile(settled, BandLowPercentile), Percentile(settled, BandHighPercentile), trend);
    }

    private static double MedianOf(IReadOnlyList<double> sorted)
        => sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2.0;

    /// <summary>Nearest-rank, so a band edge is always a reading the fleet actually produced.</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
