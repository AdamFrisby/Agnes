namespace Agnes.Plugins.CodeyBox;

/// <summary>Which slice of the backlog is on screen.</summary>
public enum SuggestionFilter
{
    /// <summary>What a developer picking up work should see first: the important ones.</summary>
    Important,

    /// <summary>Cheap and worth doing — the natural place to start a session.</summary>
    QuickWins,

    All,
}

public enum SuggestionSort
{
    /// <summary>Important before notable before minor. The default, because severity is the only field
    /// here that speaks to whether something should be done at all.</summary>
    Severity,

    /// <summary>Cheapest first, for filling a gap.</summary>
    Effort,

    Newest,
}

/// <summary>
/// Narrowing and ordering for the suggestion backlog.
///
/// <para>This exists because 162 open suggestions rendered as one flat list is not a backlog, it is a
/// wall. Every one of them here is "open", for one project, so the only things that separate them are
/// severity (13 important, 104 notable, 45 minor), category (six values) and effort (30 tiny, 4 large) —
/// and none of those were filterable, sortable or searchable.</para>
///
/// <para>It deliberately mirrors the queue's chips rather than inventing a second vocabulary: the
/// operator learns one pattern in the section they use most, and it should keep working everywhere else.</para>
/// </summary>
public static class SuggestionView
{
    /// <summary>Severity in the order it should be read, not alphabetically — "important" sorts after
    /// "minor" and before "notable" in a string comparison, which is exactly wrong.</summary>
    private static int SeverityRank(Suggestion s) => (s.Severity ?? string.Empty).ToLowerInvariant() switch
    {
        "important" => 0,
        "notable" => 1,
        "minor" => 2,
        _ => 3,
    };

    private static int EffortRank(Suggestion s) => s.Effort switch
    {
        "tiny" => 0,
        "small" => 1,
        "medium" => 2,
        "large" => 3,
        _ => 4,
    };

    public static IReadOnlyList<Suggestion> Apply(
        IEnumerable<Suggestion> all,
        SuggestionFilter filter,
        SuggestionSort sort,
        string? search,
        string? category)
    {
        IEnumerable<Suggestion> view = all;

        view = filter switch
        {
            SuggestionFilter.Important => view.Where(s => s.IsImportant),
            SuggestionFilter.QuickWins => view.Where(s => s.IsQuickWin),
            _ => view,
        };

        if (category is { Length: > 0 })
        {
            view = view.Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (search is { Length: > 0 })
        {
            // The rationale and the file list are searched as well as the title, because "which
            // suggestions touch the sandbox code" is a question about the body, not the heading.
            view = view.Where(s =>
                s.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (s.Rationale?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.FilesReferenced?.Any(f => f.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? false));
        }

        return sort switch
        {
            SuggestionSort.Severity => [.. view.OrderBy(SeverityRank).ThenBy(EffortRank).ThenByDescending(s => s.CreatedAt)],
            SuggestionSort.Effort => [.. view.OrderBy(EffortRank).ThenBy(SeverityRank)],
            _ => [.. view.OrderByDescending(s => s.CreatedAt)],
        };
    }

    /// <summary>Categories present in the data, so the filter offers what exists rather than a fixed list
    /// that could be wrong for another instance.</summary>
    public static IReadOnlyList<string> Categories(IEnumerable<Suggestion> all)
        => [.. all.Select(s => s.Category).Where(c => c is { Length: > 0 }).Distinct().Order()!];
}
