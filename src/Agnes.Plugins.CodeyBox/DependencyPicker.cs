using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Choosing what a work item waits on, from the queue rather than from memory.
/// </summary>
/// <remarks>
/// <para>The old create form asked for <c>dependsOn</c> as a comma-separated string of UUIDs, which on a
/// queue of four hundred items is a request to go and look them up somewhere else. 82 items on the live
/// instance carry dependencies and they arrive in authored batches, so the ids someone wants are nearly
/// always <i>near</i> the item they are looking at: same chain first, then same project, then everything
/// else — newest first inside each band, because a dependency on something created a month ago is rare
/// and a dependency on this morning's work is the norm.</para>
///
/// <para>Terminal items are excluded except the Failed family. A Done parent is satisfied and adding it
/// is a no-op; a Cancelled one is a trap. But a <i>Failed</i> parent is the single most useful edge to be
/// able to draw: it is the state whose retry unblocks a chain, and the pane offers exactly that.</para>
///
/// <para>Descendants of the subject are excluded outright rather than merely warned about. An edge back
/// to something that already waits on you is a cycle, and the orchestrator would reject it — but only
/// after the operator had ticked it and pressed the button.</para>
/// </remarks>
public sealed partial class DependencyPicker : ObservableObject
{
    /// <summary>Ticked ids, held apart from the candidate rows so that re-filtering by search never
    /// silently drops a tick that scrolled out of view.</summary>
    private readonly HashSet<string> _ticked = new(StringComparer.Ordinal);

    /// <summary>Everything eligible, before the search box narrows it.</summary>
    private readonly List<DependencyCandidate> _eligible = [];

    public DependencyPicker() => TickCommand = new RelayCommand<DependencyCandidate>(Tick);

    /// <summary>What is on offer, after <see cref="Search"/>. Ordered same-chain, same-project, rest.</summary>
    public ObservableCollection<DependencyCandidate> Candidates { get; } = [];

    /// <summary>The ids currently ticked. The set the caller sends, in the order they were offered.</summary>
    public IReadOnlyList<string> Ticked => [.. _eligible.Where(c => _ticked.Contains(c.Item.Id)).Select(c => c.Item.Id)];

    public int TickedCount => _ticked.Count;

    public bool HasTicked => _ticked.Count > 0;

    public bool IsEmpty => Candidates.Count == 0;

    [ObservableProperty]
    private string _search = string.Empty;

    partial void OnSearchChanged(string value) => ApplySearch();

    public IRelayCommand<DependencyCandidate> TickCommand { get; }

    /// <summary>
    /// Repoints the picker at a subject and a queue.
    /// </summary>
    /// <param name="subject">The item the edges would be drawn <i>into</i> — excluded along with
    /// everything that already waits on it. Null when composing new work, which does not exist yet and so
    /// cannot be depended on, cannot be its own parent, and has no descendants.</param>
    /// <param name="items">The whole queue. Needed whole rather than pre-filtered: the descendant walk and
    /// the same-chain test are graph questions, and a filtered list answers them wrongly.</param>
    /// <param name="preTicked">Ids to start ticked — the item the composer was opened from, or the
    /// dependencies an existing item already has.</param>
    /// <param name="near">
    /// What to band the list around, when that is not the subject. A follow-up is composed <i>from</i> an
    /// item and then <i>depends on</i> it, so that item has to stay on offer — it is the anchor for the
    /// same-chain and same-project bands but is not excluded from them. Defaults to
    /// <paramref name="subject"/>, which is the pane's case: there, the item being edited is both.
    /// </param>
    public void Reset(
        WorkItemRow? subject,
        IReadOnlyList<WorkItemRow> items,
        IEnumerable<string>? preTicked = null,
        WorkItemRow? near = null)
    {
        _ticked.Clear();
        foreach (var id in preTicked ?? [])
        {
            _ticked.Add(id);
        }

        var anchor = near ?? subject;
        var byId = items.GroupBy(i => i.Id, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var excluded = subject is null ? [] : Descendants(subject, items);
        var component = anchor is null ? [] : Component(anchor, items, byId);

        _eligible.Clear();
        _eligible.AddRange(items
            .Where(i => !excluded.Contains(i.Id))
            .Where(IsEligible)
            .Select(i => new DependencyCandidate(
                i,
                StateOf(i),
                _ticked.Contains(i.Id),
                SameChain: component.Contains(i.Id),
                SameProject: anchor is not null && i.ProjectId is { Length: > 0 } && i.ProjectId == anchor.ProjectId))
            // Band before recency: a chain-mate is the answer far more often than a stranger created an
            // hour later, and sorting by time alone buries it.
            .OrderBy(c => c.SameChain ? 0 : c.SameProject ? 1 : 2)
            .ThenByDescending(c => c.Item.CreatedAt)
            .ThenBy(c => c.Item.Id, StringComparer.Ordinal));

        Search = string.Empty;
        ApplySearch();
    }

    /// <summary>Whether an item can be depended on at all. Non-terminal, plus the Failed family — a failed
    /// parent is retryable, which is the whole point of being able to point at one.</summary>
    private static bool IsEligible(WorkItemRow item) => !item.IsTerminal || item.IsFailed;

    private void Tick(DependencyCandidate? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        if (!_ticked.Remove(candidate.Item.Id))
        {
            _ticked.Add(candidate.Item.Id);
        }

        // Records: the row is replaced rather than mutated, and Reconcile turns that into one Replace at
        // the index the operator is looking at.
        Restamp();
    }

    private void Restamp()
    {
        for (var i = 0; i < _eligible.Count; i++)
        {
            _eligible[i] = _eligible[i] with { Ticked = _ticked.Contains(_eligible[i].Item.Id) };
        }

        ApplySearch();
    }

    private void ApplySearch()
    {
        IEnumerable<DependencyCandidate> view = _eligible;
        if (Search is { Length: > 0 } search)
        {
            view = view.Where(c => Matches(c.Item, search));
        }

        Reconcile.Apply(Candidates, [.. view], c => c.Item.Id);
        OnPropertyChanged(nameof(TickedCount));
        OnPropertyChanged(nameof(HasTicked));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Ticked));
    }

    /// <summary>The four handles a person actually has on an item they are trying to find again.</summary>
    internal static bool Matches(WorkItemRow item, string search)
        => item.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (item.ExternalId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.WorkBranch?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Everything that transitively waits on <paramref name="subject"/>, plus the subject itself. Walked
    /// forwards over the reverse edges, because <c>dependsOn</c> only points at parents.
    /// </summary>
    internal static HashSet<string> Descendants(WorkItemRow subject, IReadOnlyList<WorkItemRow> items)
    {
        var found = new HashSet<string>(StringComparer.Ordinal) { subject.Id };
        var frontier = new Queue<WorkItemRow>();
        frontier.Enqueue(subject);

        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var child in items.Where(i => DependsOn(i, parent)))
            {
                if (found.Add(child.Id))
                {
                    frontier.Enqueue(child);
                }
            }
        }

        return found;
    }

    /// <summary>The connected component <paramref name="subject"/> sits in, ignoring edge direction —
    /// which is what "the same chain" means on the board.</summary>
    private static HashSet<string> Component(
        WorkItemRow subject, IReadOnlyList<WorkItemRow> items, IReadOnlyDictionary<string, WorkItemRow> byId)
    {
        var found = new HashSet<string>(StringComparer.Ordinal) { subject.Id };
        var frontier = new Queue<WorkItemRow>();
        frontier.Enqueue(subject);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();

            foreach (var parentRef in current.DependsOn ?? [])
            {
                if (Resolve(parentRef, byId, items) is { } parent && found.Add(parent.Id))
                {
                    frontier.Enqueue(parent);
                }
            }

            foreach (var child in items.Where(i => DependsOn(i, current)))
            {
                if (found.Add(child.Id))
                {
                    frontier.Enqueue(child);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether <paramref name="item"/> names <paramref name="parent"/> as a dependency.
    /// </summary>
    /// <remarks>
    /// The orchestrator lets an edge be written as a UUID, as an <c>externalId</c>, or as
    /// <c>&lt;projectId&gt;:&lt;externalId&gt;</c> — and a chain created from a pasted plan uses the
    /// externalId form for every edge, because none of the items had UUIDs when they were written. So a
    /// plain id comparison misses exactly the chains this picker exists to keep you out of.
    /// </remarks>
    private static bool DependsOn(WorkItemRow item, WorkItemRow parent)
        => (item.DependsOn ?? []).Any(reference =>
            string.Equals(reference, parent.Id, StringComparison.Ordinal)
            || (parent.ExternalId is { Length: > 0 } external
                && (string.Equals(reference, external, StringComparison.Ordinal)
                    || string.Equals(reference, $"{parent.ProjectId}:{external}", StringComparison.Ordinal))));

    private static WorkItemRow? Resolve(
        string reference, IReadOnlyDictionary<string, WorkItemRow> byId, IReadOnlyList<WorkItemRow> items)
    {
        if (byId.TryGetValue(reference, out var direct))
        {
            return direct;
        }

        // "<projectId>:<externalId>" and a bare externalId are both legal ways to name a parent.
        var external = reference.Contains(':', StringComparison.Ordinal)
            ? reference[(reference.IndexOf(':', StringComparison.Ordinal) + 1)..]
            : reference;

        return items.FirstOrDefault(i => string.Equals(i.ExternalId, external, StringComparison.Ordinal));
    }

    /// <summary>
    /// One row's state as a pip.
    /// </summary>
    /// <remarks>
    /// A local reading of the same states the board classifies, because a candidate row must be able to
    /// say what it is without a <see cref="Board"/> having been built — the composer offers candidates
    /// before there is anything to build one from. It answers the same question the same way; if the two
    /// ever disagree the model is right.
    /// </remarks>
    internal static StepState StateOf(WorkItemRow item) => item.State switch
    {
        "Done" => StepState.Done,
        "Cancelled" => StepState.Cancelled,
        _ when item.IsFailed => StepState.Failed,
        _ when item.IsActive => StepState.Running,
        _ when item.IsBlockedByDependency => StepState.Blocked,
        _ when item.IsWaiting => StepState.Parked,
        _ => StepState.Ready,
    };
}
