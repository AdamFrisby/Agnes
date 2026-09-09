namespace Agnes.Plugins.CodeyBox;

/// <summary>How a headline number should read.</summary>
public enum TileTone
{
    /// <summary>Ordinary. A number worth knowing, not worth acting on.</summary>
    Neutral,

    /// <summary>Work is moving.</summary>
    Active,

    /// <summary>Waiting on a person.</summary>
    Attention,

    /// <summary>Something is wrong or stopped.</summary>
    Bad,
}

/// <summary>
/// One number on the dashboard, with what it means and where it goes.
/// </summary>
/// <param name="Caption">Said in full rather than abbreviated, because a bare number under a one-word
/// label is a quiz.</param>
public sealed record Tile(string Label, string Value, string Caption, TileTone Tone)
{
    public bool IsNeutral => Tone == TileTone.Neutral;
    public bool IsActive => Tone == TileTone.Active;
    public bool IsAttention => Tone == TileTone.Attention;
    public bool IsBad => Tone == TileTone.Bad;
}

/// <summary>
/// The at-a-glance state of the orchestrator, derived from what the API already returns.
///
/// <para><b>The number that motivated this.</b> On this host the queue holds ten queued items and every
/// one of them is blocked on an unsatisfied dependency, so <b>nothing can start</b> — resuming the queue
/// would change nothing. The tab opened on a 404-row list that said none of that. "Queued: 10" is not
/// merely unhelpful there, it is misleading; "runnable now: 0" is the fact.</para>
/// </summary>
public static class Dashboard
{
    /// <summary>
    /// Items that could be picked up this moment: queued, and not waiting on anything.
    /// </summary>
    public static int Runnable(IEnumerable<WorkItemRow> items)
        => items.Count(i => i.State == "Queued" && i.DependsOnSatisfied);

    public static int Queued(IEnumerable<WorkItemRow> items) => items.Count(i => i.State == "Queued");

    public static int Running(IEnumerable<WorkItemRow> items) => items.Count(i => i.IsActive);

    public static int Failed(IEnumerable<WorkItemRow> items) => items.Count(i => i.IsFailed);

    public static int Blocked(IEnumerable<WorkItemRow> items)
        => items.Count(i => !i.IsTerminal && !i.DependsOnSatisfied);

    /// <summary>
    /// Whether the fleet is stopped without anyone having stopped it: the queue is running, nothing is in
    /// flight, and nothing is eligible to start. Distinct from paused, which is a decision someone made
    /// and can undo, and distinct from idle, which means there is simply no work.
    /// </summary>
    public static bool IsStalled(IEnumerable<WorkItemRow> items, bool queuePaused)
    {
        var all = items as IReadOnlyCollection<WorkItemRow> ?? [.. items];
        return !queuePaused && Running(all) == 0 && Queued(all) > 0 && Runnable(all) == 0;
    }

    /// <summary>
    /// The headline row, in the order the questions get asked: is it running, is anything eligible, what
    /// is waiting, and what needs a person.
    /// </summary>
    public static IReadOnlyList<Tile> Tiles(
        IReadOnlyList<WorkItemRow> items,
        bool queuePaused,
        int slotsInUse,
        int slotsTotal)
    {
        var running = Running(items);
        var runnable = Runnable(items);
        var queued = Queued(items);
        var failed = Failed(items);
        var blocked = Blocked(items);

        return
        [
            new Tile(
                "Queue",
                queuePaused ? "Paused" : running > 0 ? "Working" : "Idle",
                queuePaused
                    ? "nothing will be picked up until it is resumed"
                    : running > 0 ? "agents are running" : "no agent is running",
                queuePaused ? TileTone.Bad : running > 0 ? TileTone.Active : TileTone.Neutral),

            new Tile(
                "Runnable now",
                runnable.ToString(System.Globalization.CultureInfo.CurrentCulture),
                // The distinction the old list could not draw, and the reason this dashboard exists.
                queued == 0
                    ? "nothing is queued"
                    : runnable == 0
                        ? $"all {queued} queued items are waiting on dependencies"
                        : $"of {queued} queued",
                queued > 0 && runnable == 0 ? TileTone.Bad : runnable > 0 ? TileTone.Active : TileTone.Neutral),

            new Tile(
                "In flight",
                $"{running} / {slotsTotal}",
                slotsInUse >= slotsTotal && slotsTotal > 0 ? "every slot is busy" : "dispatch slots in use",
                running > 0 ? TileTone.Active : TileTone.Neutral),

            new Tile(
                "Failed",
                failed.ToString(System.Globalization.CultureInfo.CurrentCulture),
                failed == 0 ? "nothing has failed" : "need a decision from you",
                failed > 0 ? TileTone.Attention : TileTone.Neutral),

            new Tile(
                "Blocked",
                blocked.ToString(System.Globalization.CultureInfo.CurrentCulture),
                blocked == 0 ? "nothing is waiting on anything" : "waiting on a dependency to finish",
                blocked > 0 ? TileTone.Attention : TileTone.Neutral),
        ];
    }

    /// <summary>
    /// What would be picked up next, in the orchestrator's own order — priority first, then queue
    /// position. Blocked items are included rather than hidden: "the next item cannot start" is the
    /// answer, and omitting it would present the queue as healthier than it is.
    /// </summary>
    public static IReadOnlyList<WorkItemRow> NextUp(IEnumerable<WorkItemRow> items, int take = 6)
        => [.. items
            .Where(i => i.State == "Queued")
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.QueuePosition)
            .Take(take)];

    /// <summary>
    /// Pipeline health, but only when there is something to measure.
    ///
    /// <para>The orchestrator scores a window with no transitions in it as a perfect 1.0 — which is
    /// arithmetically fine and, rendered as "100% healthy", a lie: this host reports score 1 over 0
    /// transitions precisely because nothing has run. A dashboard that turns silence into a green tick is
    /// worse than one that says nothing.</para>
    /// </summary>
    public static string HealthLabel(double score, int totalTransitions)
        => totalTransitions == 0
            ? "no transitions in the last day — nothing to measure"
            : $"{score * 100:0}% clean over {totalTransitions} transition{(totalTransitions == 1 ? "" : "s")}";

    public static bool HealthIsMeaningful(int totalTransitions) => totalTransitions > 0;
}
