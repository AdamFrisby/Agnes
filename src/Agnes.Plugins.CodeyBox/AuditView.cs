namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// One audit gate's <b>run reliability</b> on a single work item: how often it was invoked, and how often
/// that invocation failed to complete.
///
/// <para><b>This is not the audit verdict, and the distinction is the whole point.</b> The orchestrator's
/// <c>agent_involvement.outcome</c> — the only per-gate signal the HTTP API exposes — takes the values
/// <c>success</c>, <c>failure:quota</c>, <c>failure:agent</c>, <c>failure:timeout</c>,
/// <c>failure:cancelled</c>, <c>failure:infrastructure</c>, <c>failure:transient</c> and
/// <c>failure:semantic-incompatible</c>. There is no value meaning "the auditor rejected the work".
/// Whether an audit actually blocked lives in <c>work_item_audit_progress</c> — 1,559 of 1,771 iterations
/// blocked, across 7,268 findings on this instance — and <b>no endpoint reads that table</b>.</para>
///
/// <para>So this reports something real and worth seeing (512 audit-phase runs here died on provider
/// quota alone) while saying plainly that it is not the verdict. Labelling run failures as "blocked"
/// would have been a confident, wrong answer to the most important question on the screen.</para>
///
/// <para>It still earns its place: the worst item on this host recorded <b>670 runs across 52
/// iterations</b>, and reading that list row by row tells you nothing.</para>
/// </summary>
/// <param name="Gate">Gate name with the shared <c>audit:</c> prefix removed.</param>
/// <param name="Runs">How many times this gate was invoked on this item.</param>
/// <param name="Blocks">How many of those invocations did not complete.</param>
/// <param name="LastFailed">Whether the most recent invocation failed.</param>
public sealed record GateSummary(string Gate, int Runs, int Blocks, bool LastFailed, DateTimeOffset LastAt)
{
    /// <summary>Whether any invocation of this gate failed to complete.</summary>
    public bool EverBlocked => Blocks > 0;

    /// <summary>Fraction of runs that blocked, for the bar. Never divides by zero.</summary>
    public double BlockFraction => Runs == 0 ? 0 : (double)Blocks / Runs;

    /// <summary>Bar width in device pixels, against a fixed 120px track.</summary>
    public double BarWidth => Math.Max(EverBlocked ? 2 : 0, BlockFraction * 120);

    public string Counts => Blocks == 0
        ? $"{Runs} run{(Runs == 1 ? "" : "s")} · all completed"
        : $"{Blocks} of {Runs} runs failed to complete";

    public string LastLabel => LastFailed ? "last run failed" : "last run ok";
}

/// <summary>
/// How far into its audit budget an item has gone.
///
/// <para>The most actionable single fact about a work item in this system, and the UI has never shown it:
/// an item on iteration 3 and one on iteration 44 looked identical. 205 of the 404 items here have
/// iterations; the deepest reached 52 against a configured ceiling of 25 — the ceiling is raised in place
/// rather than enforced, so exceeding it is both possible and worth seeing.</para>
/// </summary>
public sealed record AuditProgress(int Current, int Ceiling)
{
    public bool IsKnown => Current > 0;

    /// <summary>Past the budget it was given. Not an error — the orchestrator extends the ceiling — but
    /// the clearest signal that an item is grinding rather than converging.</summary>
    public bool OverBudget => Ceiling > 0 && Current > Ceiling;

    public double Fraction => Ceiling <= 0 ? 0 : Math.Min(1.0, (double)Current / Ceiling);

    public double BarWidth => Math.Max(Current > 0 ? 3 : 0, Fraction * 200);

    public string Label => Ceiling > 0
        ? (OverBudget
            ? $"iteration {Current} — past the {Ceiling}-iteration budget"
            : $"iteration {Current} of {Ceiling}")
        : $"iteration {Current}";
}

/// <summary>Builds the two views above from the runs the API already returns, so neither needs a new
/// endpoint. Pure functions of their input — no state, no I/O.</summary>
public static class AuditSummary
{
    /// <summary>Gate records, worst first: what blocks most, and among equals what blocked most recently.</summary>
    public static IReadOnlyList<GateSummary> Gates(IEnumerable<AgentRun> runs)
        => [.. runs
            .Where(r => r.Phase.StartsWith("audit:", StringComparison.Ordinal))
            .GroupBy(r => r.PhaseLabel)
            .Select(g =>
            {
                var ordered = g.OrderBy(r => r.StartedAt).ToList();
                return new GateSummary(
                    g.Key,
                    ordered.Count,
                    ordered.Count(r => r.Failed),
                    ordered[^1].Failed,
                    ordered[^1].StartedAt);
            })
            .OrderByDescending(s => s.Blocks)
            .ThenByDescending(s => s.LastAt)];

    /// <summary>
    /// Iteration depth against the project's ceiling. The depth is the highest iteration any run reports;
    /// the ceiling comes from the project, because the item itself does not carry one on this deployment.
    /// </summary>
    public static AuditProgress Progress(IEnumerable<AgentRun> runs, int ceiling)
        => new(runs.Select(r => r.Iteration ?? 0).DefaultIfEmpty(0).Max(), ceiling);

    /// <summary>
    /// The runs worth showing by default: everything that failed, plus everything from the newest
    /// iteration. The rest are successful gate runs that will pass again — a count, not a row.
    /// </summary>
    public static IReadOnlyList<AgentRun> Notable(IReadOnlyList<AgentRun> runs)
    {
        if (runs.Count == 0)
        {
            return runs;
        }

        var newest = runs.Select(r => r.Iteration ?? 0).DefaultIfEmpty(0).Max();
        return [.. runs.Where(r => r.Failed || (r.Iteration ?? 0) == newest || r.Iteration is null)];
    }
}
