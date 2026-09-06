namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// INFERENCE
//
// Every field the composer can fill in from where it was opened is a field the operator does not have to
// remember. The old form asked for the project id typed exactly ("codeybox-self"), which is knowledge
// the interface already had — and the reason work was filed without dependencies is that the form asked
// for a UUID nobody has memorised.
//
// The rule these follow: infer the RELATIONSHIP, never the words. A follow-up knows what it depends on
// but not what it is for; a duplicate knows both. Anything inferred is shown as a chip and is one click
// from being wrong on purpose.
// ---------------------------------------------------------------------------------------------------

public static partial class Composer
{
    /// <summary>Where Background sits: below the default, and nowhere near the floor, so it is picked up
    /// when the queue drains rather than never.</summary>
    internal const int BackgroundPriority = -100;

    /// <summary>Fills a draft in from where the composer was opened.</summary>
    /// <remarks>
    /// <paramref name="items"/> is taken but unused: every relationship these intents need is already on
    /// <see cref="ComposerContext.From"/>, and resolving ids against the list would only let a filtered
    /// board silently drop a parent it cannot see. It stays in the signature for the dependency picker,
    /// which does need the list.
    /// </remarks>
    public static Draft Infer(ComposerContext context, IReadOnlyList<Project> projects, IReadOnlyList<WorkItemRow> items)
    {
        _ = items;

        var from = context.From;
        var project = from?.ProjectId
            ?? context.Suggestion?.ProjectId
            ?? context.ProjectFilter
            ?? projects.FirstOrDefault()?.Id
            ?? string.Empty;

        var title = string.Empty;
        var prompt = string.Empty;
        IReadOnlyList<string> dependsOn = [];

        switch (context.Intent)
        {
            case ComposerIntent.FollowUp when from is not null:
                // The one thing a follow-up definitely knows.
                dependsOn = [from.Id];
                break;

            case ComposerIntent.Sibling when from is not null:
                // Beside it, not after it: the same parents, so the two can run together.
                dependsOn = from.DependsOn ?? [];
                break;

            case ComposerIntent.Split when from is not null:
                // The caller runs Parse over this prompt; the split inherits the original's place in the
                // graph so the steps land where the item was.
                prompt = from.Prompt ?? string.Empty;
                dependsOn = from.DependsOn ?? [];
                break;

            case ComposerIntent.Duplicate when from is not null:
                title = from.Title;
                prompt = from.Prompt ?? string.Empty;
                project = from.ProjectId ?? project;
                break;

            case ComposerIntent.Promote when context.Suggestion is { } suggestion:
                title = suggestion.Title;
                prompt = PromotedPrompt(suggestion);
                // Deliberately no edge to the work item the suggestion came from: that item is Done, and
                // a dependency on it would be satisfied the moment it was written. It is provenance, not
                // a prerequisite.
                break;

            default:
                break;
        }

        // A follow-up to urgent work is usually urgent too; a duplicate filed into another project is
        // not, and a promoted suggestion has no history to inherit from.
        var inherits = context.Intent is ComposerIntent.FollowUp or ComposerIntent.Sibling or ComposerIntent.Split;
        var priority = inherits && from is { Priority: not 0 } ? from.Priority : (int?)null;

        return new Draft(
            ProjectId: project,
            Title: title,
            Prompt: prompt,
            ExternalId: null,
            DependsOn: dependsOn,
            Priority: priority,
            Agent: null,
            BaseBranch: null,
            AuditMaxIterations: null,
            AuditorProfile: null,
            IsRefactor: false);
    }

    private static string PromotedPrompt(Suggestion suggestion)
    {
        var rationale = suggestion.Rationale?.Trim() ?? string.Empty;
        var files = suggestion.FilesReferenced ?? [];
        if (files.Count == 0)
        {
            return rationale;
        }

        var line = $"Files: {string.Join(", ", files)}";
        return rationale.Length == 0 ? line : $"{rationale}\n\n{line}";
    }

    /// <summary>The priority a <see cref="Position"/> maps to among the currently queued items.</summary>
    public static int PriorityFor(Position position, string? afterChainId, IReadOnlyList<Chain> next, int projectMaxPriority)
    {
        var cap = Math.Min(projectMaxPriority, BoardModel.PriorityCeiling);

        switch (position)
        {
            case Position.Next:
                // One above the current top. When the top already sits at the cap this returns the cap
                // itself, which is not enough on its own — the caller reorders from there, because the
                // only way past a ceiling is to move everything else down.
                var top = next.Count == 0 ? 0 : next.Max(c => c.Head.Priority);
                return Math.Min(next.Count == 0 ? 0 : top + 1, cap);

            case Position.After:
                // Equal priority, and created_at breaks the tie in the right direction for free: the new
                // item is younger, so it sorts after the one it was placed behind.
                var head = next.FirstOrDefault(c => string.Equals(c.Id, afterChainId, StringComparison.Ordinal));
                return head is null ? 0 : Math.Min(head.Head.Priority, cap);

            case Position.Background:
                return BackgroundPriority;

            default:
                return 0;
        }
    }
}
