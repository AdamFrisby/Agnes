namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The pure model behind the board: work items in, <see cref="Board"/> out, plus the derivations the
/// pane and the composer need. No I/O; time is injected. See <c>BoardContract.cs</c> for what each
/// output means and the orchestrator facts it rests on.
/// </summary>
public static partial class BoardModel
{
    /// <summary>Builds the runway from the full work-item list.</summary>
    public static Board Build(IReadOnlyList<WorkItemRow> items, IReadOnlyList<Project> projects, int slotsBusy, int slotsTotal, DateTimeOffset now)
        => throw new NotImplementedException("BoardModel.Build is being implemented.");

    /// <summary>What the pane shows about one item's place in its chain.</summary>
    public static Relations RelationsOf(WorkItemRow item, IReadOnlyList<WorkItemRow> items, DateTimeOffset now)
        => throw new NotImplementedException("BoardModel.RelationsOf is being implemented.");

    /// <summary>
    /// The minimal set of priority rewrites that make <paramref name="movedId"/> sit at
    /// <paramref name="newIndex"/> within the dispatch order of <paramref name="queued"/> (given in current
    /// dispatch order), respecting each item's project cap. Prefers changing only the moved item; renumbers
    /// neighbours with gaps only when there is no room.
    /// </summary>
    public static IReadOnlyList<PriorityChange> Reorder(IReadOnlyList<WorkItemRow> queued, string movedId, int newIndex, IReadOnlyDictionary<string, int> projectMaxPriority)
        => throw new NotImplementedException("BoardModel.Reorder is being implemented.");

    /// <summary>Whether adding edges from <paramref name="childId"/> to <paramref name="parentIds"/> would create a cycle.</summary>
    public static bool WouldCycle(string childId, IReadOnlyList<string> parentIds, IReadOnlyList<WorkItemRow> items)
        => throw new NotImplementedException("BoardModel.WouldCycle is being implemented.");
}

/// <summary>The composer's pure half: parsing a pasted plan and inferring a draft from context.</summary>
public static partial class Composer
{
    /// <summary>Splits pasted text into a <see cref="Plan"/>. One section is one item.</summary>
    public static Plan Parse(string text, string projectId, string prefix)
        => throw new NotImplementedException("Composer.Parse is being implemented.");

    /// <summary>Fills a draft in from where the composer was opened.</summary>
    public static Draft Infer(ComposerContext context, IReadOnlyList<Project> projects, IReadOnlyList<WorkItemRow> items)
        => throw new NotImplementedException("Composer.Infer is being implemented.");

    /// <summary>The priority a <see cref="Position"/> maps to among the currently queued items.</summary>
    public static int PriorityFor(Position position, string? afterChainId, IReadOnlyList<Chain> next, int projectMaxPriority)
        => throw new NotImplementedException("Composer.PriorityFor is being implemented.");
}
