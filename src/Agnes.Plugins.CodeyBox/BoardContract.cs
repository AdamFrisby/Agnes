namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// THE BOARD CONTRACT
//
// The work-item surface of the CodeyBox tab is a scheduler's runway, not a filtered list and not a
// Kanban. The fleet it was designed against showed why: 406 items of which ~30 are live; work arrives
// as dependency CHAINS authored in batches (9 items in one second, 7-step series, 82 items with
// dependencies); Done items waited a median of 45 hours before pickup against ~3 hours of work; and
// the operator steered pickup order by hand-typing ~70 distinct priority values. A loop has no column
// order, but a scheduler with two slots has a linear order, and that order is the plan.
//
// So the board is FOUR HORIZONS, read top to bottom:
//
//   Now      what is in flight, one row per running item
//   Next     the dispatch order, exactly as the orchestrator will pick (priority DESC, createdAt ASC)
//   Waiting  what cannot start yet, grouped by reason, each with its unblock
//   Landed   what finished, by day; older history behind search
//
// and CHAINS are the unit you read (a row shows its steps as a strip of state pips), while ITEMS remain
// the unit you act on. Creating work is a COMPOSER that infers everything it can (project, agent,
// branch, dependencies, position) and shows each inference as an overridable chip; a pasted multi-step
// plan becomes a chain in one pass using CodeyBox's external-id dependency batching.
//
// Facts about the orchestrator this contract encodes (verified in the CodeyBox source):
//   - dispatch order is `priority DESC, created_at ASC, id ASC`; POST /workitems/reorder writes only a
//     queue_position hint the dispatcher does NOT read — so reordering means rewriting priorities;
//   - priority is [-1000, 1000] globally, capped per project by Project.MaxPriority; PATCH
//     /workitems/{id}/priority works in any non-terminal state;
//   - dependsOn accepts UUIDs or externalIds; externalId is unique per project, 1–256 chars, no
//     whitespace, must not start with "wi-" and must not parse as a UUID; PATCH /workitems/{id} with
//     dependsOn is replace-set and allowed in any state;
//   - a dependency is satisfied only by Done; Failed/Cancelled parents block their children until an
//     operator retries or uncancels them, or the child drops the edge.
//
// Everything here is an immutable record or enum. No I/O, no Avalonia, no MVVM.
// ---------------------------------------------------------------------------------------------------

/// <summary>Where a chain sits on the runway.</summary>
public enum Horizon
{
    Now,
    Next,
    Waiting,
    Landed,
    /// <summary>Older than the landed window, or cancelled: reachable by search, not shown by default.</summary>
    History,
}

/// <summary>Why a waiting chain cannot start. Each reason has one unblock action.</summary>
public enum WaitReason
{
    None,
    /// <summary>Runnable, but every slot is busy. The unblock is capacity or a higher position.</summary>
    Slot,
    /// <summary>A step is waiting on a parent that has not reached Done. The unblock depends on the
    /// parent's state: wait (in flight), retry (failed), uncancel (cancelled), or drop the edge.</summary>
    Parent,
    /// <summary>Parked by the orchestrator with a resume: quota window or transient back-off.</summary>
    Quota,
    /// <summary>Needs a person: an open question, or a failed item awaiting a decision.</summary>
    Person,
    /// <summary>The queue, project queue, or the only eligible agent is paused by an operator.</summary>
    Paused,
}

/// <summary>The state of one step, as a pip in a chain strip.</summary>
public enum StepState
{
    Done,
    Running,
    /// <summary>Queued and all parents satisfied: will run when a slot frees.</summary>
    Ready,
    /// <summary>Queued behind an unsatisfied parent.</summary>
    Blocked,
    Parked,
    Failed,
    Cancelled,
    /// <summary>Waiting on a person.</summary>
    NeedsPerson,
}

/// <summary>One item as a step of its chain.</summary>
/// <param name="Index">Zero-based position in dependency order (a topological sort, stable by createdAt).</param>
/// <param name="Series">The "3/7" the title carries, when it does; empty otherwise.</param>
public sealed record Step(WorkItemRow Item, int Index, StepState State, string Series)
{
    public bool IsDone => State == StepState.Done;
    public bool IsRunning => State == StepState.Running;
    public bool IsReady => State == StepState.Ready;
    public bool IsBlocked => State == StepState.Blocked;
    public bool IsParked => State == StepState.Parked;
    public bool IsFailed => State == StepState.Failed;
    public bool IsCancelled => State == StepState.Cancelled;
    public bool NeedsPerson => State == StepState.NeedsPerson;
}

/// <summary>
/// A connected set of work items joined by dependencies, or a single item with none. The row the board
/// shows.
/// </summary>
/// <param name="Id">The root item's id (the earliest-created item with no parent inside the chain).</param>
/// <param name="Title">The chain's title: the common prefix of a numbered series ("Test selection (RTS)"),
/// else the root's title.</param>
/// <param name="Steps">Every member in dependency order.</param>
/// <param name="Head">The member the row stands for: the running step, else the next runnable, else the
/// blocking step, else the last one to finish. Selecting the row selects this item.</param>
/// <param name="Why">One line: "step 3 of 7 running on claude", "waiting on step 2, which is waiting for
/// a slot", "step 4 failed: needs a decision", "landed 14:02". Never empty.</param>
/// <param name="Blocker">For <see cref="WaitReason.Parent"/>: the parent step that holds the chain. Null
/// otherwise.</param>
/// <param name="DispatchRank">Position in the fleet-wide dispatch order among Ready items (0 = picked
/// next); -1 when the head is not Ready.</param>
/// <param name="LastActivity">Latest UpdatedAt across members.</param>
/// <param name="Landed">When the last member reached Done, for Landed rows; null otherwise.</param>
public sealed record Chain(
    string Id,
    string Title,
    string? ProjectId,
    IReadOnlyList<Step> Steps,
    WorkItemRow Head,
    Horizon Horizon,
    WaitReason Reason,
    string Why,
    Step? Blocker,
    int DispatchRank,
    DateTimeOffset LastActivity,
    DateTimeOffset? Landed)
{
    public bool IsSingleton => Steps.Count == 1;
    public int Count => Steps.Count;
    public int DoneCount => Steps.Count(s => s.IsDone);
    public string Progress => IsSingleton ? string.Empty : $"{DoneCount} of {Count}";
    public bool HasBlocker => Blocker is not null;
    public bool IsNow => Horizon == Horizon.Now;
    public bool IsNext => Horizon == Horizon.Next;
    public bool IsWaiting => Horizon == Horizon.Waiting;
    public bool IsLanded => Horizon == Horizon.Landed;
    public string Agent => Head.Agent ?? string.Empty;
    public string? Cost => Head.Cost;
}

/// <summary>A group of waiting chains that share a reason and therefore an unblock.</summary>
public sealed record WaitGroup(WaitReason Reason, string Title, IReadOnlyList<Chain> Chains)
{
    public int Count => Chains.Sum(c => c.Count);
    public string Header => Count == Chains.Count ? $"{Title}  ({Count})" : $"{Title}  ({Chains.Count} chains, {Count} items)";
}

/// <summary>One day of landed work.</summary>
/// <param name="Title">"Today", "Yesterday", then "Tue 3 Sep".</param>
public sealed record LandedDay(DateOnly Day, string Title, IReadOnlyList<Chain> Chains)
{
    public int Count => Chains.Sum(c => c.Count);
    public string Header => $"{Title}  ({Count})";
}

/// <summary>
/// The runway. Built from the full work-item list; nothing here needs a second request.
/// </summary>
/// <param name="Next">In dispatch order. Index 0 is what the orchestrator picks when a slot frees.</param>
/// <param name="Landed">Most recent day first; the window is <see cref="LandedWindowDays"/>.</param>
/// <param name="HistoryCount">Items older than the window or cancelled, reachable through search.</param>
/// <param name="Slots">Running and total dispatch slots, for the Now header ("2 of 2 slots").</param>
public sealed record Board(
    IReadOnlyList<Chain> Now,
    IReadOnlyList<Chain> Next,
    IReadOnlyList<WaitGroup> Waiting,
    IReadOnlyList<LandedDay> Landed,
    int HistoryCount,
    (int Busy, int Total) Slots)
{
    public const int LandedWindowDays = 7;

    public int NowCount => Now.Count;
    public int NextCount => Next.Sum(c => c.Count);
    public int WaitingCount => Waiting.Sum(g => g.Count);
    public int LandedCount => Landed.Sum(d => d.Count);
    public bool HasNow => Now.Count > 0;
    public bool HasNext => Next.Count > 0;
    public bool HasWaiting => Waiting.Count > 0;
    public bool HasLanded => Landed.Count > 0;
    public string NowHeader => Slots.Total > 0 ? $"Now  ·  {Slots.Busy} of {Slots.Total} slots" : "Now";
    public string NextHeader => $"Next  ·  {NextCount} in dispatch order";
    public string WaitingHeader => $"Waiting  ·  {WaitingCount}";
    public string LandedHeader => $"Landed  ·  {LandedCount} in the last {LandedWindowDays} days";
    public string HistoryLabel => HistoryCount == 0 ? string.Empty : $"{HistoryCount} older or cancelled items — search to find one";
}

/// <summary>A parent or child of the selected item, for the pane's relations band.</summary>
/// <param name="Satisfied">For a parent: whether it is Done (the only state that satisfies a dependency).</param>
public sealed record Relation(WorkItemRow Item, StepState State, bool Satisfied)
{
    public string Label => Item.Title;
}

/// <summary>What the pane shows about where an item sits in its chain.</summary>
/// <param name="Chain">The chain the item belongs to; a singleton chain for an independent item.</param>
/// <param name="BlockingRoot">The nearest ancestor that is not Done and not in flight — the item whose
/// retry, uncancel or removal would unblock this one. Null when nothing blocks.</param>
public sealed record Relations(
    WorkItemRow Item,
    IReadOnlyList<Relation> Parents,
    IReadOnlyList<Relation> Children,
    Chain Chain,
    Relation? BlockingRoot)
{
    public bool HasParents => Parents.Count > 0;
    public bool HasChildren => Children.Count > 0;
    public bool IsBlocked => BlockingRoot is not null;
    public int Position => Chain.Steps.First(s => s.Item.Id == Item.Id).Index + 1;
    public string PositionLabel => Chain.IsSingleton ? string.Empty : $"step {Position} of {Chain.Count}";
}

/// <summary>A queue position, in words. The number it maps to is always visible and editable.</summary>
public enum Position
{
    /// <summary>Ahead of everything currently queued.</summary>
    Next,
    /// <summary>Immediately after a chosen chain.</summary>
    After,
    /// <summary>The project's default (priority 0, FIFO).</summary>
    Normal,
    /// <summary>Below everything: picked only when nothing else is runnable.</summary>
    Background,
}

/// <summary>One priority rewrite the board must send to realise a new order.</summary>
public sealed record PriorityChange(string Id, int From, int To);

/// <summary>A candidate in the dependency picker: a live or queued item the new work could wait on.</summary>
/// <param name="Ticked">Pre-ticked when the composer was opened from that item ("add a follow-up").</param>
/// <param name="SameChain">Shares a chain with the launching item; sorted first.</param>
public sealed record DependencyCandidate(WorkItemRow Item, StepState State, bool Ticked, bool SameChain, bool SameProject)
{
    public string Label => Item.Title;
    public string Detail => $"{Item.State}  ·  {Item.ProjectId}  ·  {Item.ShortId}";
}

/// <summary>
/// One work item the composer will create. Every nullable field means "inherit the project's default";
/// the composer shows the inherited value as a chip rather than an empty box.
/// </summary>
/// <param name="ExternalId">Set for every draft in a plan so siblings can depend on each other before
/// any of them has an id; null for a single item unless the operator sets one.</param>
/// <param name="DependsOn">UUIDs of existing items and/or ExternalIds of sibling drafts.</param>
public sealed record Draft(
    string ProjectId,
    string Title,
    string Prompt,
    string? ExternalId,
    IReadOnlyList<string> DependsOn,
    int? Priority,
    string? Agent,
    string? BaseBranch,
    int? AuditMaxIterations,
    string? AuditorProfile,
    bool IsRefactor)
{
    public bool HasDependencies => DependsOn.Count > 0;
    public bool IsValid => !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Prompt);
}

/// <summary>
/// A pasted plan, split into drafts. Splitting happens on markdown headings, numbered sections
/// ("1.", "1)", "Step 1", "1/7"), or horizontal rules; each section's first line is its title. With one
/// section the plan is a single item. Dependencies default to LINEAR (each step waits on the one before)
/// unless a section names its own ("depends on: 2, 4" / "after step 2"), and the operator can retick
/// any edge before creating.
/// </summary>
/// <param name="Problems">Anything that would be rejected: an empty title, a cycle, a reference to a
/// step that does not exist, an invalid external id. Empty means ready to create.</param>
/// <param name="Prefix">The external-id prefix generated for this plan (e.g. "plan-20260906-1412"), so
/// sibling references resolve at create time without round trips.</param>
public sealed record Plan(IReadOnlyList<Draft> Drafts, IReadOnlyList<string> Problems, string Prefix)
{
    public bool IsChain => Drafts.Count > 1;
    public bool IsReady => Drafts.Count > 0 && Problems.Count == 0 && Drafts.All(d => d.IsValid);
    public string Summary => Drafts.Count switch
    {
        0 => "nothing to create",
        1 => "1 item",
        var n => $"{n} steps as a chain",
    };
}

/// <summary>
/// Where the composer was opened from, so it can fill in everything it already knows. Every field is
/// a hint the operator can override.
/// </summary>
/// <param name="From">The selected item, when opened from one.</param>
/// <param name="Intent">What the operator asked for: follow-up (depends on <paramref name="From"/>),
/// sibling (joins its chain at the same level), split (steps carved from its prompt), duplicate
/// (same prompt, another project), or blank.</param>
/// <param name="ProjectFilter">The project the board is filtered to, if any.</param>
/// <param name="Suggestion">The suggestion being promoted, if any: its rationale seeds the prompt and its
/// files the title.</param>
public sealed record ComposerContext(
    WorkItemRow? From,
    ComposerIntent Intent,
    string? ProjectFilter,
    Suggestion? Suggestion);

public enum ComposerIntent
{
    Blank,
    FollowUp,
    Sibling,
    Split,
    Duplicate,
    Promote,
}
