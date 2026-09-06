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
        var next = rows
            .Where(r => r.Horizon == Horizon.Next)
            .OrderByDescending(r => r.Head.Priority)
            .ThenBy(r => r.Head.CreatedAt)
            .ThenBy(r => r.Head.Id, StringComparer.Ordinal)
            .Select((r, i) => r with { DispatchRank = i, Why = WhyNext(i) })
            .ToList();

        var waiting = rows.Where(r => r.Horizon == Horizon.Waiting).ToList();
        var groups = WaitOrder
            .Select(g => new WaitGroup(
                g.Reason,
                g.Title,
                [.. waiting.Where(r => r.Reason == g.Reason).OrderByDescending(r => r.LastActivity).ThenBy(r => r.Id, StringComparer.Ordinal)]))
            .Where(g => g.Chains.Count > 0)
            .ToList();

        var landed = rows
            .Where(r => r.Horizon == Horizon.Landed && r.Landed is not null)
            .GroupBy(r => DateOnly.FromDateTime(r.Landed!.Value.ToLocalTime().DateTime))
            .OrderByDescending(g => g.Key)
            .Select(g => new LandedDay(
                g.Key,
                DayTitle(g.Key, now),
                [.. g.OrderByDescending(r => r.Landed!.Value)]))
            .ToList();

        var history = rows.Where(r => r.Horizon == Horizon.History).Sum(r => r.Count);

        return new Board(running, next, groups, landed, history, (slotsBusy, slotsTotal));
    }

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
