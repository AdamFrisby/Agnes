using System.Collections.ObjectModel;

namespace Agnes.Plugins.CodeyBox;

/// <summary>Which slice of the queue is on screen.</summary>
/// <remarks>
/// <see cref="NeedsAttention"/> is the default, and the reason this type exists. A real queue on this
/// host holds 404 items of which 322 are Done and 50 Cancelled: showing everything by default buries the
/// ten queued and twenty-two failed items that a person could actually do something about under 372 that
/// they cannot. The other filters exist because "show me everything" must still be one click away —
/// hiding history would answer a different complaint.
/// </remarks>
public enum QueueFilter
{
    NeedsAttention,
    Active,
    Done,
    Cancelled,
    All,
}

/// <summary>How the visible items are ordered.</summary>
/// <remarks>
/// Priority first because the orchestrator itself works the queue in that order, so it is the ordering
/// that predicts what happens next. Recent is for "what just changed", cost for "where did the money
/// go" — one item on this instance cost $73.
/// </remarks>
public enum QueueSort
{
    Priority,
    Recent,
    Oldest,
    Cost,
}

/// <summary>A project, as offered in a picker.</summary>
/// <remarks>
/// Exists so creating work can be a choice rather than a memory test: the form used to require the
/// project's id typed exactly ("codeybox-self"), which is knowledge the interface already had.
/// </remarks>
public sealed record ProjectChoice(string Id, string DisplayName, string? DefaultAgent)
{
    public string Label => $"{DisplayName}  ({Id})";
}

/// <summary>One project's group of work items, when the list is grouped.</summary>
public sealed class WorkItemGroup(string project, IReadOnlyList<WorkItemRow> items)
{
    public string Project { get; } = project;

    public ObservableCollection<WorkItemRow> Items { get; } = [.. items];

    /// <summary>Named with its count, so a collapsed group still says how much is inside.</summary>
    public string Header => $"{Project}  ({Items.Count})";
}
