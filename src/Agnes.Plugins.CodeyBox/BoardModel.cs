using System.Globalization;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The pure model behind the board: work items in, <see cref="Board"/> out, plus the derivations the
/// pane and the composer need. No I/O; time is injected. See <c>BoardContract.cs</c> for what each
/// output means and the orchestrator facts it rests on.
///
/// <para>Split across <c>BoardModel.*.cs</c>: chains (components, series merging, topological order),
/// horizons (head, blocker, the Why lines), and reorder (the minimal priority rewrite).</para>
/// </summary>
public static partial class BoardModel
{
    /// <summary>The order the waiting groups are read in: what needs a person, then what needs another
    /// item, then what the orchestrator will resolve by itself, then what merely needs capacity.</summary>
    private static readonly (WaitReason Reason, string Title)[] WaitOrder =
    [
        (WaitReason.Person, "Needs you"),
        (WaitReason.Parent, "Waiting on a parent"),
        (WaitReason.Quota, "Parked by the orchestrator"),
        (WaitReason.Slot, "Waiting for a slot"),
        (WaitReason.Paused, "Paused"),
    ];

    /// <summary>Builds the runway from the full work-item list.</summary>
    /// <remarks>
    /// <paramref name="projects"/> is taken but unused today: every fact the four horizons need is on
    /// the work item itself, and the project defaults belong to the composer's chips rather than to a
    /// row. It stays in the signature because the caller has the list and a future band (a per-project
    /// slot budget) would need it — passing it later would be a breaking change to every call site.
    /// </remarks>
    public static Board Build(IReadOnlyList<WorkItemRow> items, IReadOnlyList<Project> projects, int slotsBusy, int slotsTotal, DateTimeOffset now)
    {
        _ = projects;

        var rows = Chains(items).SelectMany(c => RowsFor(c, now)).ToList();

        var running = rows
            .Where(r => r.Horizon == Horizon.Now)
            .OrderByDescending(r => r.Head.UpdatedAt)
            .ThenBy(r => r.Head.Id, StringComparer.Ordinal)
            .ToList();

        // Exactly the dispatcher's own comparator. This list is a prediction, and a prediction that
        // sorts by anything else is a lie the operator will act on.
        // Phase boundaries first: an item that has finished its work phase and waits for an audit slot is
        // taken ahead of fresh queued work so the queue drains (finishing phases outrank starting ones),
        // and within them the closer to landed the sooner. Then the dispatcher's own comparator.
        var next = rows
            .Where(r => r.Horizon == Horizon.Next)
            .OrderBy(r => PhaseOrder(r.Head))
            .ThenByDescending(r => r.Head.Priority)
            .ThenBy(r => r.Head.CreatedAt)
            .ThenBy(r => r.Head.Id, StringComparer.Ordinal)
            .Select((r, i) => IsPhaseBoundary(r.Head)
                // The lede is carried separately so a whole section of these can say the wait once and
                // leave each row its own duration — see Lift.
                ? r with { DispatchRank = i, Why = WhyBoundary(r.Head, now), WhyLede = BoundaryLede(r.Head) }
                : r with { DispatchRank = i, Why = WhyNext(i) })
            .ToList();

        var waiting = rows.Where(r => r.Horizon == Horizon.Waiting).ToList();
        var groups = WaitOrder
            .Select(g => (g.Reason, g.Title, Rows: Lift(
                [.. waiting.Where(r => r.Reason == g.Reason).OrderByDescending(r => r.LastActivity).ThenBy(r => r.Id, StringComparer.Ordinal)])))
            .Where(g => g.Rows.Rows.Count > 0)
            .Select(g => new WaitGroup(g.Reason, g.Title, g.Rows.Rows) { SharedWhy = g.Rows.Shared })
            .ToList();

        var landed = rows
            .Where(r => r.Horizon == Horizon.Landed && r.Landed is not null)
            .GroupBy(r => DateOnly.FromDateTime(r.Landed!.Value.ToLocalTime().DateTime))
            .OrderByDescending(g => g.Key)
            .Select(g => (g.Key, Rows: Lift([.. g.OrderByDescending(r => r.Landed!.Value)])))
            .Select(g => new LandedDay(g.Key, DayTitle(g.Key, now), g.Rows.Rows) { SharedWhy = g.Rows.Shared })
            .ToList();

        var history = rows.Where(r => r.Horizon == Horizon.History).Sum(r => r.Count);

        var (nowRows, nowShared) = Lift(running);
        var (nextRows, nextShared) = Lift(next);

        return new Board(nowRows, nextRows, groups, landed, history, (slotsBusy, slotsTotal))
        {
            NowSharedWhy = nowShared,
            NextSharedWhy = nextShared,
        };
    }

    /// <summary>
    /// One reason, said once.
    /// </summary>
    /// <remarks>
    /// <para>A section whose rows all say the same thing is a section that said it in the wrong place:
    /// sixteen queued rows each reading "waiting for an audit slot for 10h 36m" spend sixteen rows' worth
    /// of width on one fact, and the width they spend is the title's. So the section header says it, and
    /// the rows keep only what is theirs.</para>
    ///
    /// <para>Two shapes of "the same thing", because a Why can carry a varying tail. Where the whole line
    /// matches, the header takes the whole line and the rows fall silent. Where only the
    /// <see cref="Chain.WhyLede"/> matches — the same wait, different durations — the header takes the
    /// lede and each row keeps its own tail ("for 3h 20m"), which is the part that differs and the part
    /// worth reading.</para>
    ///
    /// <para>A MAJORITY is enough, not unanimity. The live queue's Next was nine items waiting for an
    /// audit slot, one waiting to push and six merely queued behind them — and since the waiting nine sort
    /// first, every row an operator could see said the same sentence while the section as a whole did not.
    /// A header that is honest about its coverage ("9 of 16 waiting for an audit slot") lifts that, and the
    /// rows that say something else keep saying it.</para>
    ///
    /// <para>Never for a single row: a set of one does not need a quantifier, and the row has the space.</para>
    /// </remarks>
    internal static (IReadOnlyList<Chain> Rows, string Shared) Lift(IReadOnlyList<Chain> rows)
    {
        if (rows.Count < 2)
        {
            return (rows, string.Empty);
        }

        var dominant = rows
            .Where(r => r.WhyLede.Length > 0)
            .GroupBy(r => r.WhyLede, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        // Three rows saying one thing is repetition worth lifting even in a section of sixteen — the live
        // Next had seven of its rows waiting for an audit slot, sorted to the front, so every row on
        // screen said it while the section as a whole did not. The header is quantified ("7 of 16"), so
        // lifting a minority states a fact rather than implying one. A section of two lifts only when both
        // rows agree: one repetition is not a pattern, and a two-row header has nothing to gain.
        if (dominant is null || (dominant.Count() < 3 && dominant.Count() != rows.Count))
        {
            return (rows, string.Empty);
        }

        var lede = dominant.Key;
        var whole = dominant.First().Why;
        var identical = dominant.All(r => string.Equals(r.Why, whole, StringComparison.Ordinal));
        var quantifier = dominant.Count() == rows.Count
            ? "all"
            : $"{dominant.Count().ToString(CultureInfo.InvariantCulture)} of {rows.Count.ToString(CultureInfo.InvariantCulture)}";

        return (
            [.. rows.Select(r => string.Equals(r.WhyLede, lede, StringComparison.Ordinal)
                ? r with { ShownWhy = identical ? string.Empty : Tail(r.Why, lede) }
                : r)],
            $"{quantifier} {(identical ? whole : lede)}");
    }

    /// <summary>What is left of a Why once its section has said the lede.</summary>
    private static string Tail(string why, string lede)
        => why.StartsWith(lede, StringComparison.Ordinal) ? why[lede.Length..].Trim() : why;

    private static string DayTitle(DateOnly day, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        return day == today ? "Today"
            : day == today.AddDays(-1) ? "Yesterday"
            : day.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>What the pane shows about one item's place in its chain.</summary>
    public static Relations RelationsOf(WorkItemRow item, IReadOnlyList<WorkItemRow> items, DateTimeOffset now)
    {
        IReadOnlyList<WorkItemRow> known = items.Any(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal))
            ? items
            : [.. items, item];
        var byId = new Dictionary<string, WorkItemRow>(StringComparer.Ordinal);
        foreach (var row in known)
        {
            byId[row.Id] = row;
        }

        // Only Done satisfies a dependency. Failed and Cancelled parents block their children until
        // somebody retries them, uncancels them, or drops the edge — which is why the pane has to name
        // the state and not merely tick a box.
        var parents = (item.DependsOn ?? [])
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Select(p => new Relation(p, StateOf(p), p.State == "Done"))
            .ToList();

        var children = known
            .Where(c => (c.DependsOn ?? []).Contains(item.Id, StringComparer.Ordinal))
            .Select(c => new Relation(c, StateOf(c), c.State == "Done"))
            .ToList();

        var core = Chains(known).First(c => c.Steps.Any(s => string.Equals(s.Item.Id, item.Id, StringComparison.Ordinal)));
        var chain = RowsFor(core, now)[0];
        var self = core.Steps.First(s => string.Equals(s.Item.Id, item.Id, StringComparison.Ordinal));

        // Not the same walk as the row's blocker: the pane wants the item whose retry or uncancel would
        // unblock this one, so an ancestor that is merely in flight is not an answer — nobody is being
        // asked to do anything about it.
        var root = NearestStuckAncestor(core, self);

        return new Relations(item, parents, children, chain, root is null ? null : new Relation(root.Item, root.State, root.IsDone));
    }

    private static Step? NearestStuckAncestor(ChainCore core, Step from)
    {
        var byId = core.Steps.ToDictionary(s => s.Item.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from.Item.Id };
        var queue = new Queue<Step>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var step = queue.Dequeue();
            foreach (var id in step.Item.DependsOn ?? [])
            {
                if (!byId.TryGetValue(id, out var parent) || !seen.Add(id))
                {
                    continue;
                }

                if (!parent.IsDone && !parent.IsRunning)
                {
                    return parent;
                }

                queue.Enqueue(parent);
            }
        }

        return null;
    }

    /// <summary>Whether adding edges from <paramref name="childId"/> to <paramref name="parentIds"/> would create a cycle.</summary>
    public static bool WouldCycle(string childId, IReadOnlyList<string> parentIds, IReadOnlyList<WorkItemRow> items)
    {
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            edges[item.Id] = item.DependsOn ?? [];
        }

        return Reaches(edges, parentIds, childId);
    }

    /// <summary>
    /// Whether any of <paramref name="from"/> reaches <paramref name="target"/> by following
    /// <paramref name="edges"/> (a node to the nodes it depends on), <paramref name="target"/> itself
    /// counting as reached. Shared with the composer, which has to answer the same question about drafts
    /// that reference each other by external id and have no work-item ids yet.
    /// </summary>
    internal static bool Reaches(IReadOnlyDictionary<string, IReadOnlyList<string>> edges, IReadOnlyList<string> from, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(from);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (string.Equals(id, target, StringComparison.Ordinal))
            {
                return true;
            }

            if (!seen.Add(id) || !edges.TryGetValue(id, out var next))
            {
                continue;
            }

            foreach (var parent in next)
            {
                stack.Push(parent);
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="edges"/> contains a cycle at all.</summary>
    internal static bool HasCycle(IReadOnlyDictionary<string, IReadOnlyList<string>> edges)
        => edges.Keys.Any(id => edges[id].Count > 0 && Reaches(edges, edges[id], id));
}
