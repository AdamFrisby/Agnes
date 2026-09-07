using System.Globalization;
using System.Text.RegularExpressions;

namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// CHAINS
//
// The board's row is a chain, not an item, because that is how the work arrives: 82 of 404 items on the
// fleet this was designed against carry dependencies, filed in batches of seven or nine within a single
// second. A chain is therefore a connected component of the dependency graph — and, because the same
// operator also files a batch as "Test selection (RTS) 3/7: ..." without ever wiring the edges, a
// numbered series in the titles joins one too. Both are unions over the same disjoint-set structure, so
// a chain that is half explicit edges and half title-series is still one chain.
//
// Everything below is a function of (items, now). Nothing is cached, because the board rebuilds on every
// refresh and a cache keyed on a mutable list is a bug waiting for a rename.
// ---------------------------------------------------------------------------------------------------

public static partial class BoardModel
{
    /// <summary>
    /// A chain before it has been placed on the runway: the members in dependency order, with the
    /// identity the row will carry. <c>RowsFor</c> turns one of these into the one-to-many
    /// <see cref="Chain"/> rows the board shows (many when several members run at once).
    /// </summary>
    internal sealed record ChainCore(string Id, string Title, string? ProjectId, IReadOnlyList<Step> Steps);

    /// <summary>
    /// A numbered series in a title: "Test selection (RTS) 3/7: enforce Layer 0". The prefix is lazy so
    /// that a bracketed part of the name is kept rather than mistaken for the series' own brackets, and
    /// the trailing <c>[:\s]</c> is what stops "v1/2 of the parser" being read as step 1 of 2.
    /// </summary>
    [GeneratedRegex(@"^(?<prefix>.+?)\s*[\(\[]?(?<n>\d+)\s*/\s*(?<m>\d+)[\)\]]?[:\s]")]
    private static partial Regex SeriesPattern();

    /// <summary>The series a title carries, if any: its prefix, the "3/7" itself, and the total.</summary>
    internal static (string Prefix, string Series, int Total)? SeriesOf(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var m = SeriesPattern().Match(title);
        if (!m.Success)
        {
            return null;
        }

        var prefix = m.Groups["prefix"].Value.Trim();
        if (prefix.Length == 0)
        {
            return null;
        }

        var total = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        return (prefix, $"{m.Groups["n"].Value}/{m.Groups["m"].Value}", total);
    }

    /// <summary>
    /// How one item reads as a pip. Order matters: Done first, then anything in flight — which includes
    /// the phase boundaries the orchestrator passes through between an agent finishing and an audit
    /// starting. Those are not idle and must not be reported as such. Then the two Queued cases, which
    /// are the only pair a dependency can distinguish.
    /// </summary>
    internal static StepState StateOf(WorkItemRow row) => row.State switch
    {
        "Done" => StepState.Done,
        _ when row.IsActive => StepState.Running,
        // A phase boundary is not a running agent: the work phase finished and the item is waiting for
        // an audit slot (or to merge, or to push). Nothing is executing, so it is READY — it will move
        // when a slot frees — and the dispatcher takes these ahead of fresh queued work, which is why
        // the Next ordering puts them first. Drawing them as running made "0 of 2 slots" sit above ten
        // rows that said "running", and hid the fleet's actual bottleneck.
        _ when IsPhaseBoundary(row) => StepState.Ready,
        "UpstreamPushing" or "Planning" or "PlanReview" or "ReworkingForConflict" => StepState.Running,
        "Queued" => row.DependsOnSatisfied ? StepState.Ready : StepState.Blocked,
        "WaitingForQuotaReset" or "WaitingForAgentResume" or "WaitingForTransientRetry" => StepState.Parked,
        "NeedsOperatorInput" => StepState.NeedsPerson,
        _ when row.IsFailed => StepState.Failed,
        "Cancelled" => StepState.Cancelled,
        // A state this client has never heard of. Reading an unknown terminal state as Failed and an
        // unknown live one as Running is the interpretation that cannot hide work: neither is ever
        // quietly filed as Done, and neither drops off the runway into Landed.
        _ => row.IsTerminal ? StepState.Failed : StepState.Running,
    };

    /// <summary>Whether the item sits between phases: finished one, not yet picked up for the next.</summary>
    internal static bool IsPhaseBoundary(WorkItemRow row)
        => row.State is "WorkComplete" or "AuditPassed" or "Merged" or "PlanApproved";

    /// <summary>
    /// Where a phase boundary sits in the dispatcher's preference: finishing phases outrank starting ones
    /// so the queue can drain (CodeyBox #198), and the closer to landed, the sooner it goes.
    /// </summary>
    internal static int PhaseOrder(WorkItemRow row) => row.State switch
    {
        "Merged" => 0,
        "AuditPassed" => 1,
        "WorkComplete" => 2,
        "PlanApproved" => 3,
        _ => 4,
    };

    /// <summary>Every chain in the list, each with its members in dependency order.</summary>
    internal static IReadOnlyList<ChainCore> Chains(IReadOnlyList<WorkItemRow> items)
    {
        var byId = new Dictionary<string, WorkItemRow>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            byId[item.Id] = item;
        }

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            parent[item.Id] = item.Id;
        }

        foreach (var item in items)
        {
            foreach (var dep in item.DependsOn ?? [])
            {
                // An id that is not in the list is not an edge: a filtered board must not invent a chain
                // out of a parent it cannot see.
                if (byId.ContainsKey(dep))
                {
                    Union(parent, item.Id, dep);
                }
            }
        }

        var seriesRep = new Dictionary<(string? Project, string Prefix, int Total), string>();
        foreach (var item in items)
        {
            if (SeriesOf(item.Title) is not { } s)
            {
                continue;
            }

            var key = (item.ProjectId, s.Prefix.ToUpperInvariant(), s.Total);
            if (seriesRep.TryGetValue(key, out var rep))
            {
                Union(parent, item.Id, rep);
            }
            else
            {
                seriesRep[key] = item.Id;
            }
        }

        var components = new Dictionary<string, List<WorkItemRow>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var root = Find(parent, item.Id);
            if (!components.TryGetValue(root, out var bucket))
            {
                components[root] = bucket = [];
            }

            bucket.Add(item);
        }

        return [.. components.Values.Select(Core)];
    }

    private static ChainCore Core(List<WorkItemRow> members)
    {
        var ordered = TopoOrder(members);
        var ids = members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        var steps = new List<Step>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            steps.Add(new Step(row, i, StateOf(row), SeriesOf(row.Title)?.Series ?? string.Empty));
        }

        // The root is what the chain is called and where its id comes from: the earliest thing filed
        // that nothing inside the chain is waiting on.
        var root = ordered
            .Where(r => !(r.DependsOn ?? []).Any(d => ids.Contains(d) && !string.Equals(d, r.Id, StringComparison.Ordinal)))
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault() ?? ordered[0];

        var title = SeriesOf(root.Title)?.Prefix
            ?? ordered.Select(r => SeriesOf(r.Title)?.Prefix).FirstOrDefault(p => p is not null)
            ?? root.Title;

        return new ChainCore(root.Id, title, root.ProjectId, steps);
    }

    private static IReadOnlyList<WorkItemRow> TopoOrder(List<WorkItemRow> members)
    {
        var byId = members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var parents = members.ToDictionary(
            m => m.Id,
            m => (m.DependsOn ?? [])
                .Where(d => byId.ContainsKey(d) && !string.Equals(d, m.Id, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            StringComparer.Ordinal);

        var children = members.ToDictionary(m => m.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (id, ps) in parents)
        {
            foreach (var p in ps)
            {
                children[p].Add(id);
            }
        }

        var indegree = parents.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        var ready = members.Where(m => indegree[m.Id] == 0).ToList();
        var ordered = new List<WorkItemRow>(members.Count);

        while (ready.Count > 0)
        {
            // Stable by creation, then id: two steps filed in the same second still order the same way
            // on every client.
            ready.Sort(ByCreation);
            var pick = ready[0];
            ready.RemoveAt(0);
            ordered.Add(pick);

            foreach (var child in children[pick.Id])
            {
                if (--indegree[child] == 0)
                {
                    ready.Add(byId[child]);
                }
            }
        }

        if (ordered.Count < members.Count)
        {
            // A cycle the orchestrator should never have accepted. Show the rest rather than lose them.
            var placed = ordered.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            var rest = members.Where(m => !placed.Contains(m.Id)).ToList();
            rest.Sort(ByCreation);
            ordered.AddRange(rest);
        }

        return ordered;
    }

    private static int ByCreation(WorkItemRow a, WorkItemRow b)
    {
        var c = a.CreatedAt.CompareTo(b.CreatedAt);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    private static string Find(Dictionary<string, string> parent, string id)
    {
        var root = id;
        while (!string.Equals(parent[root], root, StringComparison.Ordinal))
        {
            root = parent[root];
        }

        var walk = id;
        while (!string.Equals(parent[walk], root, StringComparison.Ordinal))
        {
            var next = parent[walk];
            parent[walk] = root;
            walk = next;
        }

        return root;
    }

    private static void Union(Dictionary<string, string> parent, string a, string b)
    {
        var ra = Find(parent, a);
        var rb = Find(parent, b);
        if (!string.Equals(ra, rb, StringComparison.Ordinal))
        {
            parent[rb] = ra;
        }
    }
}
