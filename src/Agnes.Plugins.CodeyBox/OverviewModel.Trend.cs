namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// Band 3: the two series that only make sense over time.
//
//   Flow   a cumulative flow chart. Its value is the shape rather than any one number: the Done band's
//          slope is throughput, the gap above it is work in progress, and a flat Done line under a
//          widening gap is exactly what a fleet that has stopped landing things looks like.
//   Quota  a burn-down per agent to its reset. Under a subscription the marginal token is already paid
//          for, so the waste is quota left unspent when the window rolls over — not spend.
// ---------------------------------------------------------------------------------------------------

public static partial class OverviewModel
{
    /// <summary>
    /// Thirty days of cumulative counts ending today. Work created before the window is folded into the
    /// first day's baseline rather than dropped, so the series starts at the height the fleet was
    /// actually at instead of implying the queue began a month ago.
    /// </summary>
    internal static FlowSeries BuildFlow(OverviewInputs inputs)
    {
        var today = DateOnly.FromDateTime(inputs.Now.ToLocalTime().DateTime);
        var first = today.AddDays(-(FlowDays - 1));

        var created = new int[FlowDays];
        var landed = new int[FlowDays];
        var cancelled = new int[FlowDays];
        var createdBefore = 0;
        var landedBefore = 0;
        var cancelledBefore = 0;

        foreach (var item in inputs.Items)
        {
            // A default CreatedAt is the orchestrator not reporting one, not work created in year 1.
            if (item.CreatedAt != default)
            {
                Place(item.CreatedAt, created, ref createdBefore);
            }

            if (item.State == "Done")
            {
                Place(item.UpdatedAt, landed, ref landedBefore);
            }
            else if (item.State == "Cancelled")
            {
                Place(item.UpdatedAt, cancelled, ref cancelledBefore);
            }
        }

        var days = new List<FlowPoint>(FlowDays);
        var createdSoFar = createdBefore;
        var landedSoFar = landedBefore;
        var cancelledSoFar = cancelledBefore;

        for (var i = 0; i < FlowDays; i++)
        {
            createdSoFar += created[i];
            landedSoFar += landed[i];
            cancelledSoFar += cancelled[i];
            days.Add(new FlowPoint(first.AddDays(i), createdSoFar, landedSoFar, cancelledSoFar));
        }

        return new FlowSeries(days);

        void Place(DateTimeOffset at, int[] bucket, ref int before)
        {
            var day = DateOnly.FromDateTime(at.ToLocalTime().DateTime);
            var index = day.DayNumber - first.DayNumber;
            if (index < 0)
            {
                before++;
            }
            else if (index < FlowDays)
            {
                bucket[index]++;
            }
        }
    }

    /// <summary>
    /// One burn-down per agent, with the projection recomputed here so it is a function of the samples
    /// rather than something the data layer had to decide.
    /// </summary>
    internal static IReadOnlyList<QuotaBurn> BuildQuota(OverviewInputs inputs)
    {
        if (inputs.QuotaHistory.Count == 0)
        {
            // No statistics plugin on this host: the band still lists the agents and where they stand,
            // because "codex is at 4% and resets at 06:00" is most of the value even without a curve.
            return
            [
                .. inputs.Probes
                    .Where(p => p.IsKnown)
                    .Select(p => new QuotaBurn(
                        p.Agent,
                        Window: null,
                        Samples: [],
                        p.LatestSnapshot!.ResetAt,
                        p.LatestSnapshot.AvailablePct,
                        ProjectedUnspentPct: null,
                        Eligible: p is { WouldAllow: true, Paused: false })
                    {
                        ResetLabel = ResetLabelFor(inputs.Now, p.LatestSnapshot.ResetAt),
                    })
            ];
        }

        var eligible = EligibleAgents(inputs.Probes);

        return
        [
            .. inputs.QuotaHistory.Select(burn => burn with
            {
                NowPct = burn.NowPct ?? (burn.Samples.Count > 0 ? burn.Samples[^1].Pct : (double?)null),
                ProjectedUnspentPct = ProjectUnspent(burn),
                Eligible = eligible.Contains(burn.Agent, StringComparer.OrdinalIgnoreCase),
                ResetLabel = ResetLabelFor(inputs.Now, burn.ResetAt),
                SpanLabel = burn.Samples.Count >= 2 ? Inv($"last {Duration(inputs.Now - burn.Samples[0].At)}") : null,
            })
        ];
    }

    /// <summary>The reset as a clock time and a distance, because a chart edge with no label is a guess.</summary>
    internal static string? ResetLabelFor(DateTimeOffset now, DateTimeOffset? resetAt)
        => resetAt is not { } at ? null
            : at > now ? Inv($"resets {Clock(at)} · in {Duration(at - now)}")
            : Inv($"reset was due {Clock(at)}");

    /// <summary>
    /// What will be left at the reset if the current burn rate holds. Only the samples since the last
    /// refill are fitted: a window rolling over is a step change, and a line fitted across one describes
    /// nothing. A flat or rising line means the agent is not burning, so there is nothing to project.
    /// </summary>
    internal static double? ProjectUnspent(QuotaBurn burn)
    {
        if (burn.ResetAt is not { } reset)
        {
            return null;
        }

        var samples = SinceRefill(burn.Samples);
        if (samples.Count < MinBurnSamples)
        {
            return null;
        }

        var origin = samples[0].At;
        var n = (double)samples.Count;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXy = 0.0;
        var sumXx = 0.0;

        foreach (var sample in samples)
        {
            var x = (sample.At - origin).TotalSeconds;
            sumX += x;
            sumY += sample.Pct;
            sumXy += x * sample.Pct;
            sumXx += x * x;
        }

        var denominator = (n * sumXx) - (sumX * sumX);
        if (Math.Abs(denominator) < double.Epsilon)
        {
            return null;
        }

        var slope = ((n * sumXy) - (sumX * sumY)) / denominator;
        if (slope >= 0)
        {
            return null;
        }

        var intercept = (sumY - (slope * sumX)) / n;
        var projected = intercept + (slope * (reset - origin).TotalSeconds);
        return Math.Clamp(projected, 0, 100);
    }

    /// <summary>The tail of the series since the window last refilled.</summary>
    internal static IReadOnlyList<BurnSample> SinceRefill(IReadOnlyList<BurnSample> samples)
    {
        var start = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].Pct - samples[i - 1].Pct > QuotaRefillJump)
            {
                start = i;
            }
        }

        return start == 0 ? samples : [.. samples.Skip(start)];
    }
}
