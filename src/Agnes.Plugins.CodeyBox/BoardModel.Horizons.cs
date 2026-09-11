using System.Globalization;

namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// HORIZONS
//
// Placing a chain is one question asked in one order: is anything of it in flight (Now), can it start
// (Next), what stops it (Waiting), or is it over (Landed/History). The answer is read off the HEAD —
// the one member the row stands for — because a row that averaged its members would describe nothing
// that exists.
//
// The one deliberate multiplicity: a chain with two members running produces TWO rows in Now, one per
// running member. Now is a picture of the slots in use, and a chain occupying two slots has to occupy
// two lines or the header ("2 of 2 slots") contradicts the list under it.
// ---------------------------------------------------------------------------------------------------

public static partial class BoardModel
{
    /// <summary>
    /// The rows one chain contributes: one per running member when anything is running, otherwise
    /// exactly one. <see cref="Chain.DispatchRank"/> and the Next wording are filled in later, once the
    /// fleet-wide dispatch order is known — the rank is a property of the queue, not of the chain.
    /// </summary>
    internal static IReadOnlyList<Chain> RowsFor(ChainCore core, DateTimeOffset now)
    {
        var lastActivity = core.Steps.Max(s => s.Item.UpdatedAt);
        var running = core.Steps.Where(s => s.IsRunning).ToList();
        if (running.Count > 0)
        {
            return [.. running.Select(s => Row(core, s, Horizon.Now, WaitReason.None, null, WhyRunning(s), lastActivity, null))];
        }

        var head = PickHead(core.Steps);
        switch (head.State)
        {
            case StepState.Ready:
                // Runnable and not running means exactly one thing: every slot is busy.
                return [Row(core, head, Horizon.Next, WaitReason.Slot, null, WhyNext(0), lastActivity, null)];

            case StepState.Blocked:
            {
                var blocker = BlockerFor(core, head);
                return [Row(core, head, Horizon.Waiting, WaitReason.Parent, blocker, WhyParent(blocker), lastActivity, null)];
            }

            case StepState.Parked:
                return [Row(core, head, Horizon.Waiting, WaitReason.Quota, null, WhyParked(head, now), lastActivity, null)];

            case StepState.NeedsPerson:
            case StepState.Failed:
                return [Row(core, head, Horizon.Waiting, WaitReason.Person, null, WhyPerson(head), lastActivity, null)];

            default:
            {
                // Done or Cancelled with nothing live: the chain is over, and the only question left is
                // whether it is recent enough to still be on the board.
                var done = core.Steps.Where(s => s.IsDone).ToList();
                var landed = done.Count > 0 ? done.Max(s => s.Item.UpdatedAt) : (DateTimeOffset?)null;
                if (landed is null)
                {
                    return [Row(core, head, Horizon.History, WaitReason.None, null, "cancelled", lastActivity, null)];
                }

                // A pure run of Done rows is dated by its last Done; a chain that ended half-cancelled is
                // dated by whatever happened last, because that is when the operator stopped caring.
                var asOf = done.Count == core.Steps.Count ? landed.Value : lastActivity;
                var fresh = now - asOf <= TimeSpan.FromDays(Board.LandedWindowDays);
                return fresh
                    ? [Row(core, head, Horizon.Landed, WaitReason.None, null, WhyLanded(landed.Value, now), lastActivity, landed)]
                    : [Row(core, head, Horizon.History, WaitReason.None, null, WhyArchived(landed.Value), lastActivity, landed)];
            }
        }
    }

    /// <summary>
    /// The member the row stands for. Running first because that is what an operator is watching; then
    /// the next thing that could start; then whatever is stopping the chain, since a chain that cannot
    /// move is best described by the reason; and only if none of that applies, the last thing to finish.
    /// </summary>
    internal static Step PickHead(IReadOnlyList<Step> steps)
        => steps.FirstOrDefault(s => s.IsRunning)
            ?? steps.FirstOrDefault(s => s.IsReady)
            ?? steps.FirstOrDefault(s => s.IsBlocked || s.IsParked || s.NeedsPerson || s.IsFailed)
            ?? steps.LastOrDefault(s => s.IsDone)
            ?? steps[^1];

    /// <summary>
    /// The ancestor that holds <paramref name="head"/> up. A failed or cancelled ancestor wins over an
    /// in-flight one however deep it is: an in-flight parent needs nothing from anybody, and reporting
    /// it would tell the operator to wait for something that is never going to arrive.
    /// </summary>
    internal static Step? BlockerFor(ChainCore core, Step head)
    {
        var byId = core.Steps.ToDictionary(s => s.Item.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { head.Item.Id };
        var queue = new Queue<Step>();
        queue.Enqueue(head);

        Step? nearest = null;
        while (queue.Count > 0)
        {
            var step = queue.Dequeue();
            foreach (var id in step.Item.DependsOn ?? [])
            {
                if (!byId.TryGetValue(id, out var parent) || !seen.Add(id))
                {
                    continue;
                }

                if (!parent.IsDone)
                {
                    if (parent.IsFailed || parent.IsCancelled)
                    {
                        return parent;
                    }

                    nearest ??= parent;
                }

                queue.Enqueue(parent);
            }
        }

        return nearest;
    }

    private static Chain Row(
        ChainCore core,
        Step head,
        Horizon horizon,
        WaitReason reason,
        Step? blocker,
        string why,
        DateTimeOffset lastActivity,
        DateTimeOffset? landed)
        => new(core.Id, core.Title, core.ProjectId, core.Steps, head.Item, horizon, reason, why, blocker, -1, lastActivity, landed);

    // -----------------------------------------------------------------------------------------------
    // Why. One line, plain English, and never empty: a row whose state is obvious from its colour still
    // has to say what it is waiting for, because the colour cannot name the parent.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// "running on copilot", and nothing about position.
    /// </summary>
    /// <remarks>
    /// It used to read "step 3 of 9 running on copilot" beside a strip of nine pips with the third one
    /// solid, and a "3/9 steps" count beside that: one fact in three notations, of which the strip is the
    /// only one that also says which steps are done, which failed and where it stopped. So the row has a
    /// single vocabulary for position — the pips — and the Why says the one thing they cannot, which is
    /// who is on it.
    /// </remarks>
    private static string WhyRunning(Step head)
    {
        var on = string.IsNullOrWhiteSpace(head.Item.Agent) ? string.Empty : $" on {head.Item.Agent}";
        return $"running{on}";
    }

    internal static string WhyNext(int rank) => rank == 0 ? "next up" : $"{Ordinal(rank + 1)} in line";

    /// <summary>Past which a phase boundary with no movement is worth saying so — the operator's own
    /// wedge threshold, shared with the overview.</summary>
    internal static readonly TimeSpan BoundaryPatience = TimeSpan.FromMinutes(45);

    /// <summary>
    /// A phase boundary says which slot it is waiting for, and — once it has waited longer than anyone
    /// would expect a slot to take — for how long. The duration is the whole point: ten items "waiting
    /// for an audit slot" is a queue; ten items waiting for one "for 15h" with no slot in use is the
    /// fleet's bottleneck, and the row should read that way without the operator doing the arithmetic.
    /// </summary>
    internal static string WhyBoundary(WorkItemRow head, DateTimeOffset now)
    {
        var what = BoundaryLede(head);
        var quiet = now - head.UpdatedAt;
        return quiet > BoundaryPatience ? $"{what} for {Humanise(quiet)}" : what;
    }

    /// <summary>
    /// The boundary wait without its duration: the half that is the same for every item stuck at the same
    /// phase, and therefore the half a section header can say once for all of them.
    /// </summary>
    internal static string BoundaryLede(WorkItemRow head) => head.State switch
    {
        "WorkComplete" => "waiting for an audit slot",
        "AuditPassed" => "audit passed, waiting to merge",
        "Merged" => "merged, waiting to push",
        "PlanApproved" => "plan approved, waiting for a slot",
        _ => "waiting for a slot",
    };

    private static string Humanise(TimeSpan span) => span.TotalHours >= 48
        ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalMinutes >= 60
            ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"{(int)span.TotalMinutes}m";

    private static string WhyParent(Step? blocker)
    {
        if (blocker is null)
        {
            return "waiting on a parent";
        }

        var what = blocker.State switch
        {
            StepState.Ready => "is waiting for a slot",
            StepState.Running => "is running",
            StepState.Failed => "failed: needs a decision",
            StepState.Cancelled => "was cancelled",
            StepState.Parked => "is parked",
            StepState.NeedsPerson => "needs your answer",
            StepState.Blocked => "is waiting on a parent",
            _ => "has not finished",
        };

        return $"waiting on step {blocker.Index + 1}, which {what}";
    }

    private static string WhyParked(Step head, DateTimeOffset now)
    {
        var resume = new[] { head.Item.NextQuotaRetryAt, head.Item.NextTransientRetryAt }
            .Where(t => t is { } v && v > now)
            .Select(t => t!.Value)
            .OrderBy(t => t)
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();

        return resume is { } at
            ? $"parked until {at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : "waiting for quota";
    }

    private static string WhyPerson(Step head)
        => head.Item.State == "NeedsOperatorInput" ? "needs your answer" : "failed: needs a decision";

    private static string WhyLanded(DateTimeOffset landed, DateTimeOffset now)
    {
        var local = landed.ToLocalTime();
        return DateOnly.FromDateTime(local.DateTime) == DateOnly.FromDateTime(now.ToLocalTime().DateTime)
            ? $"landed {local.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            // One day format across the whole board — "Tue 8 Sep", the same shape the day headings and the
            // archive line use. It previously read "landed Tue 14:02" here and "landed Tue 8 Sep" two
            // sections down, which is two conventions for one kind of fact.
            : $"landed {local.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}";
    }

    private static string WhyArchived(DateTimeOffset landed)
        => $"landed {landed.ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture)}";

    /// <summary>"1st", "2nd", "3rd", "11th", "21st". Said in words the way a queue position is read out.</summary>
    internal static string Ordinal(int n)
    {
        var suffix = n % 100 is >= 11 and <= 13
            ? "th"
            : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
