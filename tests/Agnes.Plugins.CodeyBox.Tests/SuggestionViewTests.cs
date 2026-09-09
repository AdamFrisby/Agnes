using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Narrowing and ordering the suggestion backlog, against the live distribution: 162 open suggestions,
/// all one project, all one state — 13 important, 104 notable, 45 minor; 30 tiny effort, 4 large.
/// </summary>
public sealed class SuggestionViewTests
{
    private static Suggestion S(
        string id, string title, string severity, string category, string effort,
        string? rationale = null, string[]? files = null, int ageDays = 0)
        => new(
            Id: id,
            SourceWorkItemId: "src",
            ProjectId: "codeybox-self",
            Title: title,
            Rationale: rationale,
            Category: category,
            Severity: severity,
            EstimatedEffort: effort,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-ageDays),
            State: "open",
            PromotedToWorkItemId: null,
            FilesReferenced: files);

    private static readonly Suggestion[] Backlog =
    [
        S("1", "Fix the sandbox leak", "important", "security", "medium", files: ["src/Sandbox/Incus.cs"]),
        S("2", "Tidy the docs", "minor", "docs", "tiny", rationale: "docs/plugins.md disagrees with the code"),
        S("3", "Split the god object", "notable", "refactor", "large"),
        S("4", "Add missing tests", "notable", "test-coverage", "small", files: ["tests/Foo.cs"]),
    ];

    [Fact]
    public void Severity_sorts_by_meaning_not_alphabetically()
    {
        // "important" < "minor" < "notable" as strings, which is exactly the wrong order.
        var view = SuggestionView.Apply(Backlog, SuggestionFilter.All, SuggestionSort.Severity, null, null);

        Assert.Equal("important", view[0].Severity);
        Assert.Equal("minor", view[^1].Severity);
    }

    [Fact]
    public void Effort_sorts_cheapest_first()
    {
        var view = SuggestionView.Apply(Backlog, SuggestionFilter.All, SuggestionSort.Effort, null, null);

        Assert.Equal(["tiny", "small", "medium", "large"], view.Select(s => s.Effort));
    }

    [Fact]
    public void Important_is_the_default_slice_and_narrows_to_what_argues_for_action()
    {
        var view = SuggestionView.Apply(Backlog, SuggestionFilter.Important, SuggestionSort.Severity, null, null);

        Assert.Equal(["1"], view.Select(s => s.Id));
    }

    [Fact]
    public void Quick_wins_are_tiny_and_small_only()
    {
        var view = SuggestionView.Apply(Backlog, SuggestionFilter.QuickWins, SuggestionSort.Effort, null, null);

        Assert.Equal(["2", "4"], view.Select(s => s.Id));
    }

    [Fact]
    public void Search_reaches_the_rationale_and_the_referenced_files_not_only_the_title()
    {
        // "which suggestions touch the sandbox code" is a question about the body, not the heading.
        Assert.Equal(["1"],
            SuggestionView.Apply(Backlog, SuggestionFilter.All, SuggestionSort.Severity, "Incus.cs", null)
                .Select(s => s.Id));

        Assert.Equal(["2"],
            SuggestionView.Apply(Backlog, SuggestionFilter.All, SuggestionSort.Severity, "disagrees", null)
                .Select(s => s.Id));
    }

    [Fact]
    public void Category_narrows_and_is_offered_from_the_data_rather_than_a_fixed_list()
    {
        Assert.Equal(["docs", "refactor", "security", "test-coverage"], SuggestionView.Categories(Backlog));

        Assert.Equal(["3"],
            SuggestionView.Apply(Backlog, SuggestionFilter.All, SuggestionSort.Severity, null, "refactor")
                .Select(s => s.Id));
    }

    [Fact]
    public void Filters_combine_rather_than_replace_each_other()
    {
        var view = SuggestionView.Apply(
            Backlog, SuggestionFilter.QuickWins, SuggestionSort.Severity, null, "test-coverage");

        Assert.Equal(["4"], view.Select(s => s.Id));
    }

    [Fact]
    public void A_slice_that_matches_nothing_is_empty_rather_than_falling_back_to_everything()
        => Assert.Empty(SuggestionView.Apply(
            Backlog, SuggestionFilter.Important, SuggestionSort.Severity, "no such text", null));

    [Fact]
    public void Files_are_summarised_rather_than_listed_without_end()
    {
        var many = S("9", "t", "minor", "other", "tiny",
            files: ["a.cs", "b.cs", "c.cs", "d.cs", "e.cs", "f.cs"]);

        Assert.Contains("+2 more", many.Files);
    }
}
