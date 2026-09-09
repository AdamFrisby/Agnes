namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// REORDER
//
// Dragging a row is the one board gesture that has no matching API call. POST /workitems/reorder writes
// a queue_position the dispatcher never reads; the dispatcher reads `priority DESC, created_at ASC,
// id ASC`. So "move this to third" is really "rewrite priorities until the dispatcher's own sort says
// third", and the only honest implementation is to compute that rewrite and verify it by re-sorting.
//
// Two things make it more than arithmetic. First, created_at is a real tie-break, not a formality: two
// items at the same priority are already ordered, so moving between them often costs nothing at all.
// Second, priority is capped per project, so the obvious value ("one above the item you dropped it on")
// is sometimes illegal for this item and legal for its neighbour — which is why a failed placement
// falls back to renumbering a run rather than to clamping and hoping.
// ---------------------------------------------------------------------------------------------------

public static partial class BoardModel
{
    /// <summary>The orchestrator's global priority range.</summary>
    internal const int PriorityFloor = -1000;

    internal const int PriorityCeiling = 1000;

    /// <summary>The gap a renumbered run aims for, so the next drag has room to land in without one.</summary>
    internal const int RenumberGap = 10;

    /// <summary>
    /// The minimal set of priority rewrites that make <paramref name="movedId"/> sit at
    /// <paramref name="newIndex"/> within the dispatch order of <paramref name="queued"/> (given in current
    /// dispatch order), respecting each item's project cap. Prefers changing only the moved item; renumbers
    /// neighbours with gaps only when there is no room.
    /// </summary>
    public static IReadOnlyList<PriorityChange> Reorder(IReadOnlyList<WorkItemRow> queued, string movedId, int newIndex, IReadOnlyDictionary<string, int> projectMaxPriority)
    {
        var order = queued.ToList();
        var from = order.FindIndex(r => string.Equals(r.Id, movedId, StringComparison.Ordinal));
        if (from < 0)
        {
            return [];
        }

        var moved = order[from];
        order.RemoveAt(from);
        var pos = Math.Clamp(newIndex, 0, order.Count);
        order.Insert(pos, moved);

        var desired = order.Select(r => r.Id).ToList();
        if (Sorted(order, []).SequenceEqual(desired, StringComparer.Ordinal))
        {
            return [];
        }

        var above = pos > 0 ? order[pos - 1] : null;
        var below = pos < order.Count - 1 ? order[pos + 1] : null;
        var cap = Cap(moved, projectMaxPriority);

        // One change is always preferable to many, and among one-change answers the tightest value is:
        // sit on a neighbour's priority when created_at already orders it correctly (costing no numeric
        // room at all), and only then open a gap.
        foreach (var candidate in Candidates(above, below))
        {
            if (candidate > cap || candidate < PriorityFloor || candidate == moved.Priority)
            {
                continue;
            }

            IReadOnlyList<PriorityChange> one = [new PriorityChange(moved.Id, moved.Priority, candidate)];
            if (Sorted(order, one).SequenceEqual(desired, StringComparer.Ordinal))
            {
                return one;
            }
        }

        // No single value works. Renumber the smallest contiguous run around the drop point that opens
        // one — growing downwards first, because the room a descending sequence needs is below it.
        var lo = pos;
        var hi = pos;
        while (true)
        {
            if (TryRenumber(order, lo, hi, projectMaxPriority) is { } changes
                && Sorted(order, changes).SequenceEqual(desired, StringComparer.Ordinal))
            {
                return changes;
            }

            if (hi < order.Count - 1)
            {
                hi++;
            }
            else if (lo > 0)
            {
                lo--;
            }
            else
            {
                // The whole queue was renumbered and it still does not sort: the request is impossible
                // inside the orchestrator's range. Changing nothing is the only safe answer.
                return [];
            }
        }
    }

    /// <summary>Values worth trying for the moved item, tightest first.</summary>
    private static IEnumerable<int> Candidates(WorkItemRow? above, WorkItemRow? below)
    {
        if (below is not null)
        {
            yield return below.Priority;
        }

        if (above is not null)
        {
            yield return above.Priority;
        }

        if (below is not null)
        {
            yield return below.Priority + 1;
        }
        else if (above is not null)
        {
            yield return above.Priority - 1;
        }

        // Nothing above: the top of the list. Reaching for the ceiling is a last resort — it burns the
        // whole range — but it is the one value guaranteed to be first if any is.
        if (above is null)
        {
            yield return PriorityCeiling;
        }
    }

    /// <summary>
    /// Assigns a strictly descending run of priorities to <c>order[lo..hi]</c> that fits between the
    /// untouched neighbours on either side, honouring every item's own cap. Strictly descending rather
    /// than tie-broken because a renumber is already the expensive path — buying certainty with the
    /// numeric room is the right trade there.
    /// </summary>
    private static IReadOnlyList<PriorityChange>? TryRenumber(List<WorkItemRow> order, int lo, int hi, IReadOnlyDictionary<string, int> projectMaxPriority)
    {
        var upper = lo > 0 ? order[lo - 1].Priority - 1 : PriorityCeiling;
        var lower = hi < order.Count - 1 ? order[hi + 1].Priority + 1 : PriorityFloor;
        if (upper < lower)
        {
            return null;
        }

        var count = hi - lo + 1;
        for (var gap = RenumberGap; gap >= 1; gap--)
        {
            var values = new int[count];
            var ceiling = upper;
            var ok = true;

            for (var i = 0; i < count; i++)
            {
                var value = Math.Min(ceiling, Cap(order[lo + i], projectMaxPriority));
                if (value < lower)
                {
                    ok = false;
                    break;
                }

                values[i] = value;
                ceiling = value - gap;
            }

            if (!ok)
            {
                continue;
            }

            var changes = new List<PriorityChange>();
            for (var i = 0; i < count; i++)
            {
                var item = order[lo + i];
                if (item.Priority != values[i])
                {
                    changes.Add(new PriorityChange(item.Id, item.Priority, values[i]));
                }
            }

            return changes;
        }

        return null;
    }

    /// <summary>The item's ceiling: its project's cap, never above the orchestrator's own.</summary>
    private static int Cap(WorkItemRow row, IReadOnlyDictionary<string, int> projectMaxPriority)
        => Math.Min(
            PriorityCeiling,
            row.ProjectId is { } p && projectMaxPriority.TryGetValue(p, out var cap) ? cap : PriorityCeiling);

    /// <summary>
    /// The ids <paramref name="items"/> would dispatch in once <paramref name="changes"/> were applied.
    /// Every answer this file produces is checked through here rather than argued for: the sort is the
    /// specification, so re-running it is the only proof that counts.
    /// </summary>
    internal static IReadOnlyList<string> Sorted(IReadOnlyList<WorkItemRow> items, IReadOnlyList<PriorityChange> changes)
    {
        var applied = changes.ToDictionary(c => c.Id, c => c.To, StringComparer.Ordinal);
        return
        [
            .. items
                .OrderByDescending(r => applied.TryGetValue(r.Id, out var p) ? p : r.Priority)
                .ThenBy(r => r.CreatedAt)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => r.Id)
        ];
    }
}
