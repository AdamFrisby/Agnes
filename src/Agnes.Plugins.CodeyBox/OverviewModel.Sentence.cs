namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// The sentence.
//
// One line, the way you would say it to a colleague who asked "how's it going?". It names the constraint
// first — the single thing that, if removed, would let the fleet go faster — and then says what is
// actually happening, so the line never ends on a complaint.
//
// The priority order is the order in which a constraint makes the ones below it irrelevant: a paused
// queue makes everything else moot, a wedge is the only motion state that is a defect, no eligible agent
// makes free slots meaningless, full slots make runnable work meaningless, and so on down to "moving".
// ---------------------------------------------------------------------------------------------------

public static partial class OverviewModel
{
    private static (string Sentence, TileTone Verdict) BuildSentence(OverviewInputs inputs, FleetCounts counts)
    {
        List<string> leads = [];
        var verdict = TileTone.Neutral;

        if (inputs.Queue is { IsPaused: true } queue)
        {
            leads.Add(PausedClause(queue));
            verdict = TileTone.Bad;
        }

        if (counts.Wedged > 0)
        {
            leads.Add(Inv($"{counts.Wedged} {Plural(counts.Wedged, "item", "items")} wedged."));
            verdict = TileTone.Bad;
        }

        var constraint = Constraint(inputs, counts);
        if (constraint is { } found)
        {
            leads.Add(found.Clause);
            if (leads.Count == 1)
            {
                verdict = found.Verdict;
            }
        }

        if (leads.Count == 0)
        {
            var (clause, tone) = Status(counts, primary: true);
            return (clause, tone);
        }

        // At most three clauses: two of what is wrong, then one of what is happening. A fourth clause is
        // a paragraph, and a paragraph is not read.
        var status = Status(counts, primary: false).Clause;
        return (string.Join(" ", leads.Take(2).Append(status)), verdict);
    }

    private static string PausedClause(QueueStatus queue)
    {
        var reason = queue.PausedReason is { Length: > 0 } r ? r.TrimEnd('.') : null;

        return (queue.PausedAt, reason) switch
        {
            ({ } at, { } why) => Inv($"Paused since {Clock(at)}: {why}."),
            ({ } at, null) => Inv($"Paused since {Clock(at)}."),
            (null, { } why) => Inv($"Paused: {why}."),
            _ => "Paused.",
        };
    }

    /// <summary>The first constraint that applies, in the order that makes the ones below it moot.</summary>
    private static (string Clause, TileTone Verdict)? Constraint(OverviewInputs inputs, FleetCounts counts)
    {
        if (counts.EligibleAgents == 0 && counts.Runnable > 0)
        {
            var next = NextReset(inputs);
            return (
                next is { } reset
                    ? Inv($"Quota-bound. No agent is eligible; next reset {Clock(reset.At)} ({reset.Agent}).")
                    : "Quota-bound. No agent is eligible.",
                TileTone.Bad);
        }

        if ((inputs.Concurrency?.IsSaturated ?? false) && counts.Runnable > 0)
        {
            return (
                Inv($"Capacity-bound. {counts.SlotsBusy} of {counts.SlotsTotal} slots busy, {counts.Runnable} runnable waiting."),
                TileTone.Active);
        }

        // The state the old dashboard was built for: ten queued items, every one of them waiting on
        // something, so resuming the queue would start nothing at all.
        if (counts.Queued > 0 && counts.Runnable == 0 && counts.Running == 0)
        {
            return (
                counts.Queued == 1
                    ? "Dependency-bound. The one queued item waits on something that has not finished."
                    : Inv($"Dependency-bound. All {counts.Queued} queued items wait on something that has not finished."),
                TileTone.Bad);
        }

        if (counts.NeedsPerson > 0)
        {
            return (Inv($"Waiting on you: {WaitingParts(counts)}."), TileTone.Attention);
        }

        return null;
    }

    private static string WaitingParts(FleetCounts counts)
    {
        List<string> parts = [];
        if (counts.OpenQuestions > 0)
        {
            parts.Add(Inv($"{counts.OpenQuestions} {Plural(counts.OpenQuestions, "question", "questions")}"));
        }

        if (counts.FailedItems > 0)
        {
            parts.Add(Inv($"{counts.FailedItems} failed {Plural(counts.FailedItems, "item", "items")}"));
        }

        return parts.Count > 0
            ? string.Join(", ", parts)
            : Inv($"{counts.NeedsPerson} {Plural(counts.NeedsPerson, "item", "items")} needing a decision");
    }

    /// <summary>The earliest reset any probed agent has published.</summary>
    private static (string Agent, DateTimeOffset At)? NextReset(OverviewInputs inputs)
    {
        var probe = inputs.Probes
            .Where(p => p.IsKnown && p.LatestSnapshot!.ResetAt > inputs.Now)
            .OrderBy(p => p.LatestSnapshot!.ResetAt!.Value)
            .FirstOrDefault();

        return probe is null ? null : (probe.Agent, probe.LatestSnapshot!.ResetAt!.Value);
    }

    /// <summary>
    /// What IS happening, so the line always ends on the fleet rather than on the complaint. Said in
    /// full as the whole sentence when nothing is constraining anything, and shortened when it is only
    /// the tail of one.
    /// </summary>
    private static (string Clause, TileTone Verdict) Status(FleetCounts counts, bool primary)
    {
        if (counts.Running == 0 && counts.Queued == 0)
        {
            return (primary ? "Idle. Nothing queued." : "Nothing queued.", TileTone.Neutral);
        }

        if (counts.Running == 0)
        {
            var waiting = Inv($"{counts.Queued} queued, nothing in flight, {counts.Landed7d} landed this week.");
            return (primary ? "Waiting. " + waiting : waiting, TileTone.Neutral);
        }

        var agents = Listed(counts.RunningAgents);
        var flight = primary && agents.Length > 0
            ? Inv($"{counts.Running} in flight on {agents}")
            : Inv($"{counts.Running} in flight");

        var moving = Inv($"{flight}, {counts.Landed7d} landed this week.");
        return (primary ? "Moving. " + moving : moving, TileTone.Active);
    }
}
