using System.Globalization;

namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// THE OVERVIEW CONTRACT
//
// The CodeyBox tab opens on an overview built to answer three questions about a fleet of autonomous
// agents, in this order, in about five seconds:
//
//   1. Is it moving?       (motion — per item, rolled up to the fleet)
//   2. Is it converging?   (direction — the shape of an item's audit loop, not its iteration count)
//   3. Did it land whole?  (output — landed changes, with a complete final audit)
//
// It is deliberately NOT organised around iteration counts, first-pass yield or cost. The fleet this was
// designed against landed 325 of 406 items at a median of 9–12 audit iterations with 88% of iterations
// blocking along the way: repetition is the mechanism, not the defect. Cost is fenced by budgets and is
// a guardrail here, never a headline.
//
// The composition is three bands:
//
//   Band 1  a generated sentence naming the current constraint, plus five vitals with sparklines and a
//           control band (normal = within the fleet's own trailing norm, not an absolute)
//   Band 2  one trace per live item, worst first; healthy items collapse to a count
//   Band 3  the trend: a cumulative flow chart over 30 days, and quota burn-down per agent to its reset
//
// This file is the contract between the three parts that build it: the pure model (OverviewModel), the
// data layer that feeds it (CodeyBoxClient + CodeyBoxSectionsViewModel), and the view. Everything here
// is an immutable record or enum. No I/O, no Avalonia, no MVVM.
// ---------------------------------------------------------------------------------------------------

/// <summary>Whether a live item is doing anything. The first question asked of every row.</summary>
public enum Motion
{
    /// <summary>State or audit progress advanced recently. Leave it alone.</summary>
    Moving,

    /// <summary>Stopped on purpose with a known resume: quota window, transient back-off, paused agent.
    /// Not a problem; the resume time is the information.</summary>
    Parked,

    /// <summary>Stopped waiting on something outside the pipeline: an unsatisfied dependency, or a person
    /// (an open question, a decision on a failed item).</summary>
    Blocked,

    /// <summary>Stopped with no reason the orchestrator can give: a live state whose item and stream have
    /// both gone quiet past the phase's threshold. The one motion state that is a defect.</summary>
    Wedged,
}

/// <summary>The shape of an item's audit loop. Read from the trace, so it is a picture before it is a
/// word.</summary>
public enum Convergence
{
    /// <summary>Not enough iterations to have a shape.</summary>
    New,

    /// <summary>Blocking findings trending down. The loop is doing its job.</summary>
    Converging,

    /// <summary>Findings going down and back up, typically the same gate repeating. Iteration without
    /// progress: the one kind of repetition that is waste.</summary>
    Oscillating,

    /// <summary>Flat and non-zero: the same finding count for several iterations running.</summary>
    Stuck,

    /// <summary>Passed its last iteration; waiting on merge or push.</summary>
    Passed,
}

/// <summary>One audit iteration on the trace.</summary>
/// <param name="Complete">False while auditors are still reporting (the trailing, in-progress bar).</param>
/// <param name="SameGateAsPrevious">True when the blocking auditors are the same set as the previous
/// iteration — the signal that distinguishes oscillation from ordinary rework.</param>
public sealed record TracePoint(int Iteration, int BlockingFindings, bool Complete, bool SameGateAsPrevious);

/// <summary>
/// One live item as the overview shows it: what it is, whether it moves, which way it is heading.
/// </summary>
/// <param name="Points">The audit loop, oldest first. Empty for an item that has not reached audit.</param>
/// <param name="Ceiling">The project's iteration cap for this item; 0 when unknown.</param>
/// <param name="Why">One short line the row shows under the title: the resume time for a parked item,
/// the dependency for a blocked one, the quiet duration for a wedged one, the gate for an oscillating
/// one. Never empty for anything that is not Moving+Converging.</param>
/// <param name="NearCeiling">Within a few iterations of <paramref name="Ceiling"/> while still
/// converging — the case where extending the ceiling preserves work that would otherwise be discarded.</param>
/// <param name="NeedsPerson">An open question or a failed item awaiting a decision: blocked on you.</param>
/// <param name="SinceMoved">Time since the item's state or audit progress last advanced.</param>
/// <param name="Rank">Sort key, ascending: wedged, oscillating, near ceiling, stuck, blocked on a
/// person, parked, blocked on a dependency, then moving. Lower is more urgent.</param>
public sealed record ItemTrace(
    WorkItemRow Item,
    IReadOnlyList<TracePoint> Points,
    int Ceiling,
    Motion Motion,
    Convergence Shape,
    string Why,
    bool NearCeiling,
    bool NeedsPerson,
    TimeSpan SinceMoved,
    int Rank)
{
    /// <summary>Whether this row belongs in the attention band or collapses into the healthy count.</summary>
    public bool NeedsAttention => Motion != Motion.Moving || Shape is Convergence.Oscillating or Convergence.Stuck || NearCeiling || NeedsPerson;

    public int LastIteration => Points.Count == 0 ? 0 : Points[^1].Iteration;

    public bool HasTrace => Points.Count > 0;

    /// <summary>What the bars are, in words, because a bar chart with no caption was read as noise:
    /// "audit findings · 16 rounds".</summary>
    public string TraceCaption => Points.Count == 1 ? "audit findings · 1 round" : FormattableString.Invariant($"audit findings · {Points.Count} rounds");
    public bool IsMoving => Motion == Motion.Moving;
    public bool IsParked => Motion == Motion.Parked;
    public bool IsBlocked => Motion == Motion.Blocked;
    public bool IsWedged => Motion == Motion.Wedged;
    public bool IsOscillating => Shape == Convergence.Oscillating;
}

/// <summary>
/// Items stopped at the same phase boundary — an audit slot, a merge, a push — that nothing on this screen
/// can release. They are not moving and there is nothing to decide, so the attention band folds them into
/// one line per boundary rather than listing twenty rows that say the same thing.
/// </summary>
/// <param name="Title">"18 items waiting for an audit slot".</param>
/// <param name="Detail">How long the stillest has been quiet, and that it is not the operator's move.</param>
public sealed record AttentionGroup(string Title, string Detail, IReadOnlyList<ItemTrace> Items)
{
    public int Count => Items.Count;
}

/// <summary>Which way a vital is heading against its own norm.</summary>
public enum Trend
{
    /// <summary>Too little history to say. Shown as no arrow, never as flat.</summary>
    Unknown,
    Up,
    Flat,
    Down,
}

/// <summary>
/// One headline number with its trend. The tone is set only by leaving the control band, so a quiet
/// day reads as quiet rather than as a collapse.
/// </summary>
/// <param name="Spark">Recent samples, oldest first, for the sparkline. Empty when there is no history yet.</param>
/// <param name="Median">Trailing median the band is centred on; null when history is too short.</param>
/// <param name="BandLow">Lower edge of normal; null with <paramref name="Median"/>.</param>
/// <param name="BandHigh">Upper edge of normal; null with <paramref name="Median"/>.</param>
/// <param name="Current">The value as a number, for the sparkline's last point.</param>
public sealed record Vital(
    string Label,
    string Value,
    string Caption,
    TileTone Tone,
    IReadOnlyList<double> Spark,
    double? Median,
    double? BandLow,
    double? BandHigh,
    double Current,
    Trend Trend)
{
    public bool HasBand => Median is not null;
    public bool HasSpark => Spark.Count >= 2;

    /// <summary>What span the sparkline and band cover — "last 3h 50m", "8 weeks, by week". A sparkline
    /// with no stated period is a shape with no meaning; this is what makes it one.</summary>
    public string Period { get; init; } = string.Empty;
    public bool HasPeriod => Period.Length > 0;
    public bool IsNeutral => Tone == TileTone.Neutral;
    public bool IsActive => Tone == TileTone.Active;
    public bool IsAttention => Tone == TileTone.Attention;
    public bool IsBad => Tone == TileTone.Bad;
}

/// <summary>One day of the cumulative flow chart. All three are cumulative counts as of end of day.</summary>
public sealed record FlowPoint(DateOnly Day, int Created, int Landed, int Cancelled)
{
    /// <summary>What is in the pipeline that day: created but neither landed nor cancelled.</summary>
    public int InFlight => Math.Max(0, Created - Landed - Cancelled);
}

/// <summary>
/// The cumulative flow chart's data: the Done band's slope is throughput, the gap above it is WIP, and a
/// flat Done line under a widening gap is the picture of a stuck fleet. Built from the work-item list
/// alone (created/updated timestamps), so it is exact for what it shows and shows nothing it cannot
/// derive.
/// </summary>
public sealed record FlowSeries(IReadOnlyList<FlowPoint> Days)
{
    public bool HasData => Days.Count >= 2 && Days[^1].Created > 0;

    /// <summary>Where the landed band starts: everything that had landed before the window opened. The
    /// chart draws from here rather than from zero, so a month's work is not a sliver on top of a year's.</summary>
    public int Floor => Days.Count == 0 ? 0 : Days[0].Landed;
    /// <summary>The top of the stack as the chart draws it: landed, in flight, and only the cancellations
    /// that happened inside the window.</summary>
    public int Peak => Days.Count == 0 ? 0 : Days.Max(d => d.Landed + d.InFlight + d.Cancelled - Days[0].Cancelled);
    public int LandedInWindow => Days.Count == 0 ? 0 : Days[^1].Landed - Days[0].Landed;
    public int CancelledInWindow => Days.Count == 0 ? 0 : Days[^1].Cancelled - Days[0].Cancelled;
    public int InFlightNow => Days.Count == 0 ? 0 : Days[^1].InFlight;
    public bool HasCancelled => CancelledInWindow > 0;
    public string LandedLegend => FormattableString.Invariant($"+{LandedInWindow} landed");
    public string InFlightLegend => FormattableString.Invariant($"{InFlightNow} in flight");
    public string CancelledLegend => FormattableString.Invariant($"+{CancelledInWindow} cancelled");
}

/// <summary>One quota sample.</summary>
public sealed record BurnSample(DateTimeOffset At, double Pct);

/// <summary>
/// One agent's quota as a card: the longest window drawn as a burn-down, because that is the one whose
/// unspent remainder at reset is the waste, and every shorter window as a gauge of where it stands now,
/// because a five-hour window inside a seven-day one refills many times before the big one does and its
/// history is a sawtooth that says little.
/// </summary>
public sealed record QuotaCard(string Agent, QuotaBurn Primary, IReadOnlyList<QuotaBurn> Others)
{
    public bool HasOthers => Others.Count > 0;
}
/// <summary>
/// One agent's quota window drawn as a burn-down to its reset. Under subscriptions the marginal token is
/// free and unspent quota at the reset is the only waste, so the number that matters is
/// <paramref name="ProjectedUnspentPct"/>, not spend.
/// </summary>
/// <param name="Window">The provider's window name (five_hour, seven_day); null for the overall reading.</param>
/// <param name="Samples">Oldest first. Empty when the statistics plugin is not available on this host.</param>
/// <param name="NowPct">The latest reading; null when unknown.</param>
/// <param name="ProjectedUnspentPct">Straight-line projection of what will be left at <paramref name="ResetAt"/>
/// from the recent burn rate; null when there is no reset or too little history.</param>
/// <param name="Eligible">Whether the router would dispatch to this agent right now.</param>
public sealed record QuotaBurn(
    string Agent,
    string? Window,
    IReadOnlyList<BurnSample> Samples,
    DateTimeOffset? ResetAt,
    double? NowPct,
    double? ProjectedUnspentPct,
    bool Eligible)
{
    public bool HasSamples => Samples.Count >= 2;
    public string Label => Window is { Length: > 0 } w ? $"{Agent} · {w.Replace('_', ' ')}" : Agent;

    /// <summary>The window's name as a person says it: "seven day", "5h rolling", or "overall".</summary>
    public string WindowShort => Window is { Length: > 0 } w ? w.Replace('_', ' ').Replace('-', ' ') : "overall";

    /// <summary>"resets 06:54 · in 8h 44m" — the right edge of the chart, in words.</summary>
    public string? ResetLabel { get; init; }
    /// <summary>"last 6h 12m" — how far back the samples reach; the left edge of the chart, in words.</summary>
    public string? SpanLabel { get; init; }

    /// <summary>A projection only means something when the line is going down. A flat window at 100%
    /// with a dashed tail reads as broken; it is merely an agent nobody dispatched to.</summary>
    public bool IsBurning => ProjectedUnspentPct is { } projected && NowPct is { } now && projected < now - 1;

    public string? ProjectionLabel => IsBurning
        ? FormattableString.Invariant($"leaves ~{ProjectedUnspentPct:0}% unspent at reset")
        : ProjectedUnspentPct is not null || IsFlat ? "not burning" : null;

    /// <summary>No sample differs from the last by more than half a point: nothing was spent.</summary>
    public bool IsFlat => HasSamples && Samples.All(s => Math.Abs(s.Pct - Samples[^1].Pct) <= 0.5);
}

/// <summary>
/// A point in time the overview records about itself, so the vitals can carry a sparkline and a control
/// band. Persisted locally by the plugin (the orchestrator keeps no such series); one sample per refresh,
/// thinned to hourly beyond a day and daily beyond a week.
/// </summary>
public sealed record OverviewSample(
    DateTimeOffset At,
    int Landed7d,
    int InMotion,
    int Parked,
    int Blocked,
    int Wedged,
    int EligibleAgents,
    int SlotsBusy,
    int SlotsTotal,
    double InfraFailureRate,
    int BlockedOnYou);

/// <summary>Per-item audit progress, as the data layer hands it to the model.</summary>
public sealed record ItemAuditProgress(string WorkItemId, IReadOnlyList<AuditProgressRow> Rows);

/// <summary>
/// Everything the model needs, gathered by the data layer in one pass. Nullable members are surfaces the
/// orchestrator may not offer on a given host; the model degrades honestly rather than inventing a value.
/// </summary>
/// <param name="Items">The full work-item list.</param>
/// <param name="AuditProgress">Audit progress for the live (non-terminal) items only. Missing items simply
/// have no trace.</param>
/// <param name="Questions">Open question counts by work-item id, for the live items.</param>
/// <param name="QuotaHistory">Recent quota samples by agent and window; empty when the statistics plugin
/// is off.</param>
/// <param name="History">This plugin's own recent samples, oldest first, for sparklines and bands.</param>
/// <param name="Ceilings">Audit iteration cap by project id, from <c>/projects</c>.</param>
/// <summary>
/// One item's active agent time: the sum of its runs (work, audit, rework), with a run still open counted
/// to now. Waiting — for a slot, for quota, for a person — is not in it, which is what makes it a cost per
/// item rather than a calendar.
/// </summary>
public sealed record ItemEffort(string Id, TimeSpan Active, bool Landed, DateTimeOffset At)
{
    /// <summary>
    /// The time an item has had an agent on it. Two rules the raw run list needs:
    /// <list type="bullet">
    /// <item>Runs that overlap count once. Auditors run in parallel, one run each, and a rework can be
    /// recorded over a still-open audit; the drain estimate wants the slot's time, not the sum of every
    /// auditor's clock.</item>
    /// <item>A run with no end counts to <paramref name="now"/> only when it is the item's latest run and
    /// the item is active right now. Anything else without an end is a run a crash or a restart never
    /// closed — the live instance had one open since June — and counting it to now is how "time already
    /// spent" came to 164 days and the estimate to "under a minute".</item>
    /// </list>
    /// </summary>
    public static TimeSpan ActiveTime(IEnumerable<AgentRun> runs, DateTimeOffset now, bool active)
    {
        var list = runs.ToList();
        var latest = list.Count == 0 ? null : list.MaxBy(r => r.StartedAt);
        var spans = new List<(DateTimeOffset Start, DateTimeOffset End)>(list.Count);
        foreach (var run in list)
        {
            DateTimeOffset end;
            if (run.EndedAt is { } ended)
            {
                end = ended;
            }
            else if (active && ReferenceEquals(run, latest))
            {
                end = now;
            }
            else
            {
                continue;
            }
            if (end > run.StartedAt)
            {
                spans.Add((run.StartedAt, end));
            }
        }
        var total = TimeSpan.Zero;
        DateTimeOffset? openStart = null;
        DateTimeOffset? openEnd = null;
        foreach (var (start, end) in spans.OrderBy(s => s.Start))
        {
            if (openEnd is { } current && start <= current)
            {
                openEnd = end > current ? end : current;
                continue;
            }
            if (openStart is { } s0 && openEnd is { } e0)
            {
                total += e0 - s0;
            }
            openStart = start;
            openEnd = end;
        }
        if (openStart is { } s1 && openEnd is { } e1)
        {
            total += e1 - s1;
        }
        return total;
    }
}

/// <summary>
/// How long the queue takes to drain at today's pace: what remains times the median active time of the
/// last landed items, less the active time the in-flight items have already had, over the slots.
/// </summary>
public sealed record BurnEstimate(
    int Remaining,
    int Sampled,
    TimeSpan MedianPerItem,
    TimeSpan LowPerItem,
    TimeSpan HighPerItem,
    TimeSpan SpentOnLive,
    TimeSpan WorkRemaining,
    int Slots,
    TimeSpan Wall,
    IReadOnlyList<double> SparkHours);

public sealed record OverviewInputs(
    DateTimeOffset Now,
    IReadOnlyList<WorkItemRow> Items,
    IReadOnlyList<ItemAuditProgress> AuditProgress,
    IReadOnlyDictionary<string, int> Questions,
    QueueStatus? Queue,
    Concurrency? Concurrency,
    IReadOnlyList<QuotaProbe> Probes,
    IReadOnlyList<QuotaBurn> QuotaHistory,
    TransitionHealth? Health,
    IReadOnlyList<OverviewSample> History,
    IReadOnlyDictionary<string, int> Ceilings)
{
    /// <summary>Active time per item, for the last landed items and everything not yet terminal. Empty on
    /// a host whose agent history is off; the drain estimate then simply does not appear.</summary>
    public IReadOnlyList<ItemEffort> Effort { get; init; } = [];
}

/// <summary>
/// The overview, ready to draw.
/// </summary>
/// <param name="Sentence">The state of the world in one line, the way you would say it to a colleague:
/// "Quota-bound. Codex gated until 06:00, Claude carrying 3 of 3 slots. 2 items wedged." Names the
/// constraint first. Never empty: an idle fleet says so.</param>
/// <param name="Verdict">The one-word rating the sentence expands: the tone the tab's header wears.</param>
/// <param name="Vitals">Exactly five, in reading order: landed this week; in motion / stopped; eligible
/// capacity; infra failure rate; blocked on you.</param>
/// <param name="Attention">Live items needing a look, most urgent first (by <see cref="ItemTrace.Rank"/>).</param>
/// <param name="Healthy">Live items that are moving and converging, collapsed behind a count.</param>
/// <param name="Sample">The sample this build contributes to the history.</param>
public sealed record Overview(
    string Sentence,
    TileTone Verdict,
    IReadOnlyList<Vital> Vitals,
    IReadOnlyList<ItemTrace> Attention,
    IReadOnlyList<ItemTrace> Healthy,
    FlowSeries Flow,
    IReadOnlyList<QuotaBurn> Quota,
    OverviewSample Sample)
{
    public int HealthyCount => Healthy.Count;
    public bool HasAttention => Attention.Count > 0;
    public bool HasHealthy => Healthy.Count > 0;
    public bool HasQuota => Quota.Count > 0;
    public string HealthyLabel => Healthy.Count == 1 ? "1 item converging normally" : $"{Healthy.Count} items converging normally";

    /// <summary>Stopped at a phase boundary nothing here can release, one group per boundary. Not in
    /// <see cref="Attention"/>: they need a slot, not a look.</summary>
    public IReadOnlyList<AttentionGroup> Folded { get; init; } = [];

    /// <summary>One card per agent: the longest window as the chart, the rest as gauges beside it.</summary>
    public IReadOnlyList<QuotaCard> QuotaCards => OverviewModel.Cards(Quota, Sample.At);

    /// <summary>Time to drain the queue at today's pace; null without enough landed items to price one.</summary>
    public BurnEstimate? Burn { get; init; }
    public bool HasFolded => Folded.Count > 0;

    /// <summary>Every clock on this screen is local time; this is the one place that says so.</summary>
    public string AsOf => FormattableString.Invariant(
        $"as of {Sample.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} · times are local");
}
