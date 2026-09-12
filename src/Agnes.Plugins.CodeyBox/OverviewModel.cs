using System.Globalization;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The pure model behind the overview: <see cref="OverviewInputs"/> in, <see cref="Overview"/> out. No
/// I/O, no time source of its own (<see cref="OverviewInputs.Now"/> is injected), so every derivation is
/// a function the tests can pin. See <c>OverviewContract.cs</c> for what each output means.
///
/// <para>Split across <c>OverviewModel.*.cs</c>: traces (motion and convergence per item), vitals (the
/// five headline numbers with their control bands), the sentence, and the two trend series.</para>
/// </summary>
public static partial class OverviewModel
{
    // -----------------------------------------------------------------------------------------------
    // Thresholds. Every number this model judges by lives here, with why it is that number. None of them
    // is a fleet-wide absolute except where an absolute is genuinely what is meant (a clock window, a
    // sample count); the trend bands are always relative to the fleet's own trailing history.
    // -----------------------------------------------------------------------------------------------

    /// <summary>A live phase whose item and audit stream have both been silent this long is wedged. The
    /// operator's own rule from months of running this fleet: audits are slow and deserve patience, but
    /// three quarters of an hour with no state change and no fresh audit row is not patience, it is a
    /// stuck process nobody has noticed.</summary>
    internal static readonly TimeSpan WedgeAfter = TimeSpan.FromMinutes(45);

    /// <summary>How long a runnable queued item may sit before the row says so. Below this it is just
    /// the dispatcher's next tick, and saying "waiting for a slot" about it would be noise.</summary>
    internal static readonly TimeSpan QueuedPatience = TimeSpan.FromMinutes(30);

    /// <summary>"Landed this week" is a rolling seven days, not a calendar week: a calendar week resets
    /// the headline to zero every Monday morning for reasons that have nothing to do with the fleet.</summary>
    internal static readonly TimeSpan LandedWindow = TimeSpan.FromDays(7);

    /// <summary>Samples younger than this are excluded from a vital's control band, so the current
    /// reading cannot pull the band it is being judged against towards itself.</summary>
    internal static readonly TimeSpan BandSettleAge = TimeSpan.FromHours(1);

    /// <summary>How close to the ceiling counts as near it. Three iterations is roughly one more audit
    /// round on this fleet — the last moment at which raising the cap still saves the work.</summary>
    internal const int NearCeilingWithin = 3;

    /// <summary>Oscillation is judged over a short recent window; a sawtooth from twenty iterations ago
    /// that has since settled is history, not a live problem.</summary>
    internal const int OscillationWindow = 6;

    /// <summary>One rise is ordinary — a rework that uncovered something. Two is a pattern.</summary>
    internal const int OscillationRises = 2;

    /// <summary>Down-up-down-up with no repeated gate is still oscillation if it turns often enough.</summary>
    internal const int OscillationDirectionChanges = 3;

    /// <summary>Identical non-zero findings for this many complete iterations running is stuck, not
    /// slow: the loop is producing the same verdict and the rework is not touching it.</summary>
    internal const int StuckRun = 3;

    /// <summary>Below this many complete iterations a trace has no shape worth naming.</summary>
    internal const int ShapeMinPoints = 3;

    /// <summary>Fewer settled samples than this and a band would be an opinion rather than a norm, so
    /// the vital reports no band and a trend of <see cref="Trend.Unknown"/>.</summary>
    internal const int BandMinSamples = 8;

    /// <summary>Settled samples must also reach back at least this far. Eight readings from one afternoon
    /// are that afternoon, not a norm — and "vs a usual 0" under a fresh install is what they produced.</summary>
    internal static readonly TimeSpan BandMinSpan = TimeSpan.FromHours(24);

    /// <summary>"Landed this week" has a norm the orchestrator already holds: the previous weeks' landings,
    /// read off the items themselves. This many prior rolling weeks build it, and it takes at least
    /// <see cref="LandedNormMinWeeks"/> of them before it is used instead of the local history.</summary>
    internal const int LandedNormWeeks = 8;
    internal const int LandedNormMinWeeks = 4;

    /// <summary>A week in which nothing landed is a week the fleet was off, not a data point about its
    /// pace; only weeks with a landing count, looked for this far back.</summary>
    internal const int LandedNormLookbackWeeks = 26;
    /// <summary>The control band is the middle 60% of the fleet's own recent history (nearest-rank
    /// percentiles, so the edges are always real observed readings).</summary>
    internal const double BandLowPercentile = 20;

    internal const double BandHighPercentile = 80;

    /// <summary>How far from the trailing median counts as a direction rather than noise.</summary>
    internal const double TrendDeadband = 0.10;

    /// <summary>How many history points a sparkline draws. Wide enough to show a shape, short enough
    /// that a week-old excursion does not flatten today.</summary>
    internal const int SparkWindow = 48;

    /// <summary>The cumulative flow chart's window.</summary>
    internal const int FlowDays = 30;

    /// <summary>How many of the most recently landed items price the next one. Twenty is the operator's
    /// number: enough that one runaway item does not set the pace, few enough that last month's fleet does
    /// not either. The median, not the mean, for the same reason.</summary>
    internal const int BurnSample = 20;
    internal const int BurnMinSample = 3;

    /// <summary>A jump up of more than this many percentage points between consecutive quota samples is
    /// a window refill, not a burn — the fit has to start again after it.</summary>
    internal const double QuotaRefillJump = 15;

    /// <summary>Two points make a line through noise; three make a rate worth extrapolating.</summary>
    internal const int MinBurnSamples = 3;

    /// <summary>Infra failure rates above these read as bad and as worth attention respectively. The bad
    /// edge only applies once enough transitions have happened for the rate to mean anything.</summary>
    internal const double InfraBadRate = 0.25;

    internal const int InfraBadMinTransitions = 20;

    internal const double InfraAttentionRate = 0.10;

    // Rank, ascending: the order the attention band is read in. Lower is more urgent.
    internal const int RankWedged = 0;
    internal const int RankOscillating = 1;
    internal const int RankNearCeiling = 2;
    internal const int RankStuck = 3;
    internal const int RankNeedsPerson = 4;
    internal const int RankParked = 5;
    internal const int RankBlocked = 6;
    internal const int RankMoving = 7;

    /// <summary>Builds the whole overview in one pass.</summary>
    public static Overview Build(OverviewInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var traces = BuildTraces(inputs);
        var counts = Count(inputs, traces);

        var vitals = BuildVitals(inputs, counts);
        var (sentence, verdict) = BuildSentence(inputs, counts);
        var (folded, attention) = Fold([.. traces.Where(t => t.NeedsAttention).Order(AttentionOrder)]);
        var burn = BuildBurn(inputs);
        if (burn is not null)
        {
            vitals = [.. vitals, Drain(burn)];
        }
        return new Overview(
            sentence,
            verdict,
            vitals,
            attention,
            [.. traces.Where(t => !t.NeedsAttention).OrderBy(t => t.SinceMoved)],
            BuildFlow(inputs),
            BuildQuota(inputs),
            new OverviewSample(
                inputs.Now,
                counts.Landed7d,
                counts.Moving,
                counts.Parked,
                counts.Blocked,
                counts.Wedged,
                counts.EligibleAgents,
                counts.SlotsBusy,
                counts.SlotsTotal,
                inputs.Health?.InfraFailureRate ?? 0,
                counts.NeedsPerson))
        {
            Folded = folded,
            Burn = burn,
        };
    }

    /// <summary>
    /// The drain estimate: (items not yet terminal × median active time of the last landed items) − active
    /// time the in-flight items already had, spread over the slots. Null until enough items have landed to
    /// call the median a price.
    /// </summary>
    internal static BurnEstimate? BuildBurn(OverviewInputs inputs)
    {
        var sample = inputs.Effort
            .Where(e => e.Landed)
            .OrderByDescending(e => e.At)
            .Take(BurnSample)
            .OrderBy(e => e.At)
            .ToList();
        if (sample.Count < BurnMinSample)
        {
            return null;
        }
        List<double> hours = [.. sample.Select(e => e.Active.TotalHours)];
        List<double> sorted = [.. hours.Order()];
        var median = MedianOf(sorted);
        var remaining = inputs.Items.Where(i => !i.IsTerminal).ToList();
        var spentBy = inputs.Effort.Where(e => !e.Landed).ToDictionary(e => e.Id, e => e.Active, StringComparer.Ordinal);
        // Item by item, not in aggregate: an item that has already had ten times the median (a rework loop
        // that has run for weeks) is credited its own price and no more, or its excess would pay for every
        // other item in the queue and the estimate would read "under a minute" — as it did.
        var price = TimeSpan.FromHours(median);
        var spent = TimeSpan.Zero;
        var work = TimeSpan.Zero;
        var longest = TimeSpan.Zero;
        foreach (var item in remaining)
        {
            var had = spentBy.TryGetValue(item.Id, out var t) ? t : TimeSpan.Zero;
            var credited = had < price ? had : price;
            spent += credited;
            var left = price - credited;
            work += left;
            if (left > longest)
            {
                longest = left;
            }
        }
        var slots = Math.Max(1, inputs.Concurrency?.GlobalMaxConcurrent ?? 1);
        // Spread over the slots — but never faster than the longest single item, which is what bounds the
        // clock once fewer items remain than there are slots.
        var wall = TimeSpan.FromTicks(work.Ticks / slots);
        if (wall < longest)
        {
            wall = longest;
        }
        return new BurnEstimate(
            remaining.Count,
            sample.Count,
            TimeSpan.FromHours(median),
            TimeSpan.FromHours(Percentile(sorted, BandLowPercentile)),
            TimeSpan.FromHours(Percentile(sorted, BandHighPercentile)),
            spent,
            work,
            slots,
            wall,
            hours);
    }

    /// <summary>The estimate as a vital: the wall-clock figure, and the arithmetic said out loud.</summary>
    private static Vital Drain(BurnEstimate burn)
    {
        var value = burn.Remaining == 0 ? "nothing queued" : Inv($"~{Duration(burn.Wall)}");
        var caption = burn.Remaining == 0
            ? Inv($"an item costs {Duration(burn.MedianPerItem)} of agent time lately")
            : Inv($"{burn.Remaining} {Plural(burn.Remaining, "item", "items")} × {Duration(burn.MedianPerItem)} median − {Duration(burn.SpentOnLive)} already spent, over {burn.Slots} {Plural(burn.Slots, "slot", "slots")}");
        return new Vital(
            "Time to drain",
            value,
            caption,
            TileTone.Neutral,
            burn.SparkHours,
            burn.MedianPerItem.TotalHours,
            burn.LowPerItem.TotalHours,
            burn.HighPerItem.TotalHours,
            burn.Remaining == 0 ? 0 : burn.Wall.TotalHours,
            Trend.Unknown)
        {
            Period = Inv($"last {burn.Sampled} landed"),
        };
    }
    /// <summary>
    /// The phase boundary an item is stopped at, when that is the only thing wrong with it — or null for
    /// an item that needs a look for its own reasons (a person, a repeating gate, a budget, a silent agent
    /// mid-phase). Only the former folds: twenty rows saying "waiting for an audit slot" are one fact about
    /// the audit stage, and the operator asked, rightly, why they were being shown at all.
    /// </summary>
    internal static string? FoldKey(ItemTrace trace)
    {
        if (trace.NeedsPerson || trace.NearCeiling || trace.Shape is Convergence.Oscillating or Convergence.Stuck)
        {
            return null;
        }
        return trace.Motion switch
        {
            Motion.Wedged => trace.Item.State switch
            {
                "WorkComplete" => "waiting for an audit slot",
                "AuditPassed" => "audit passed, waiting to merge",
                "Merged" => "merged, waiting to push",
                "PlanApproved" => "plan approved, waiting for a slot",
                _ => null,
            },
            // Queued behind a parent step, or parked until quota or a retry timer: the pipeline's own
            // waits, released by the pipeline.
            Motion.Blocked => "waiting on a dependency",
            Motion.Parked => "parked until quota or a retry",
            _ => null,
        };
    }

    private static string FoldNote(string key, ItemTrace stillest) => key switch
    {
        "waiting on a dependency" => "released by their parent steps · not your move",
        "parked until quota or a retry" => Inv($"{stillest.Why} · not your move"),
        _ => Inv($"quiet up to {Duration(stillest.SinceMoved)} · not your move: they need a slot, not a look"),
    };
    internal static (IReadOnlyList<AttentionGroup> Folded, IReadOnlyList<ItemTrace> Attention) Fold(IReadOnlyList<ItemTrace> attention)
    {
        var groups = new List<AttentionGroup>();
        var rest = new List<ItemTrace>();
        foreach (var byBoundary in attention.GroupBy(FoldKey))
        {
            if (byBoundary.Key is null)
            {
                rest.AddRange(byBoundary);
                continue;
            }
            var items = byBoundary.OrderByDescending(t => t.SinceMoved).ToList();
            groups.Add(new AttentionGroup(
                Inv($"{items.Count} {Plural(items.Count, "item", "items")} {byBoundary.Key}"),
                FoldNote(byBoundary.Key, items[0]),
                items));
        }
        return ([.. groups.OrderByDescending(g => g.Count)], rest);
    }

    /// <summary>Rank first, then the one that has been still longest, then the one that matters most.</summary>
    private static readonly Comparer<ItemTrace> AttentionOrder = Comparer<ItemTrace>.Create((a, b) =>
    {
        var byRank = a.Rank.CompareTo(b.Rank);
        if (byRank != 0)
        {
            return byRank;
        }

        var byStillness = b.SinceMoved.CompareTo(a.SinceMoved);
        return byStillness != 0 ? byStillness : b.Item.Priority.CompareTo(a.Item.Priority);
    });

    /// <summary>The fleet counts every other part of the build reads, taken once.</summary>
    internal readonly record struct FleetCounts(
        int Moving,
        int Parked,
        int Blocked,
        int Wedged,
        int NeedsPerson,
        int OpenQuestions,
        int FailedItems,
        int Landed7d,
        int Queued,
        int Runnable,
        int Running,
        int EligibleAgents,
        int TotalAgents,
        int SlotsBusy,
        int SlotsTotal,
        IReadOnlyList<string> RunningAgents);

    private static FleetCounts Count(OverviewInputs inputs, IReadOnlyList<ItemTrace> traces)
    {
        var eligible = EligibleAgents(inputs.Probes);

        return new FleetCounts(
            Moving: traces.Count(t => t.Motion == Motion.Moving),
            Parked: traces.Count(t => t.Motion == Motion.Parked),
            Blocked: traces.Count(t => t.Motion == Motion.Blocked),
            Wedged: traces.Count(t => t.Motion == Motion.Wedged),
            NeedsPerson: traces.Count(t => t.NeedsPerson),
            OpenQuestions: traces.Where(t => t.NeedsPerson).Sum(t => Questions(inputs, t.Item.Id)),
            FailedItems: traces.Count(t => t.Item.IsFailed),
            Landed7d: inputs.Items.Count(i => i.State == "Done" && inputs.Now - i.UpdatedAt <= LandedWindow),
            Queued: Dashboard.Queued(inputs.Items),
            Runnable: Dashboard.Runnable(inputs.Items),
            Running: Dashboard.Running(inputs.Items),
            EligibleAgents: eligible.Count,
            TotalAgents: inputs.Probes.Select(p => p.Agent).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            SlotsBusy: inputs.Concurrency?.CurrentlyRunningTotal ?? 0,
            SlotsTotal: inputs.Concurrency?.GlobalMaxConcurrent ?? 0,
            RunningAgents:
            [
                .. inputs.Items
                    .Where(i => i.IsActive && !string.IsNullOrWhiteSpace(i.Agent))
                    .Select(i => i.Agent!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            ]);
    }

    /// <summary>The agents the router would dispatch to right now, one entry per agent.</summary>
    internal static IReadOnlyList<string> EligibleAgents(IReadOnlyList<QuotaProbe> probes)
        => [.. probes
            .Where(p => p is { WouldAllow: true, Paused: false })
            .Select(p => p.Agent)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static int Questions(OverviewInputs inputs, string itemId)
        => inputs.Questions.TryGetValue(itemId, out var n) ? n : 0;

    // ---- Small shared formatting. Numbers invariant; clock times local, as the rest of this plugin. ----

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    /// <summary>A duration said the way a person says it: "12m", "1h 12m", "2d 3h".</summary>
    internal static string Duration(TimeSpan span)
    {
        var minutes = (int)Math.Floor(Math.Max(0, span.TotalMinutes));
        if (minutes < 1)
        {
            return "under a minute";
        }

        if (minutes < 60)
        {
            return Inv($"{minutes}m");
        }

        var hours = minutes / 60;
        if (hours < 24)
        {
            var rest = minutes % 60;
            return rest == 0 ? Inv($"{hours}h") : Inv($"{hours}h {rest}m");
        }

        var days = hours / 24;
        var spare = hours % 24;
        return spare == 0 ? Inv($"{days}d") : Inv($"{days}d {spare}h");
    }

    /// <summary>"claude", "claude and codex", "claude, codex and antigravity".</summary>
    internal static string Listed(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        2 => Inv($"{names[0]} and {names[1]}"),
        _ => Inv($"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"),
    };
}
