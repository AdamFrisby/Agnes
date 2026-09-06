using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The composer's pure half. The plans here are the shapes work actually arrives in — an agent's
/// markdown, a numbered list, a paragraph — because the point of the parser is that the operator does
/// not have to reformat anything before pasting it.
/// </summary>
public sealed class ComposerTests
{
    private static DateTimeOffset Local(int day, int hour) => new(new DateTime(2026, 9, day, hour, 0, 0, DateTimeKind.Local));

    private static readonly DateTimeOffset Now = Local(6, 15);

    private static WorkItemRow Item(
        string id,
        string title = "an item",
        string? prompt = "do the thing",
        string[]? dependsOn = null,
        int priority = 0,
        string? project = "codeybox-self")
        => new(
            Id: id,
            Title: title,
            State: "Queued",
            Agent: "claude",
            ProjectId: project,
            QueuePosition: 0,
            UpdatedAt: Now,
            LastError: null,
            Prompt: prompt,
            DependsOn: dependsOn,
            Priority: priority,
            CreatedAt: Now.AddHours(-1));

    // -----------------------------------------------------------------------------------------------
    // Parse
    // -----------------------------------------------------------------------------------------------

    private const string MarkdownPlan = """
        ## Test selection (RTS) 1/7: inventory the suites
        Walk every csproj and record what it runs.

        ## Test selection (RTS) 2/7: build the file-to-test map
        Static analysis over the project graph.

        ## Test selection (RTS) 3/7: enforce Layer 0
        No test may reach the network.

        ## Test selection (RTS) 4/7: wire the selector
        Take the changed set, emit a filter.

        ## Test selection (RTS) 5/7: shadow-run it
        Run both for a week and diff.

        ## Test selection (RTS) 6/7: turn it on for PRs
        Behind a flag.

        ## Test selection (RTS) 7/7: delete the old path
        And the flag with it.
        """;

    [Fact]
    public void A_pasted_markdown_plan_becomes_a_chain_in_one_pass()
    {
        var plan = Composer.Parse(MarkdownPlan, "codeybox-self", "plan-20260906");

        Assert.Equal(7, plan.Drafts.Count);
        Assert.True(plan.IsChain);
        Assert.True(plan.IsReady);
        Assert.Equal("7 steps as a chain", plan.Summary);
        Assert.Equal("Test selection (RTS) 1/7: inventory the suites", plan.Drafts[0].Title);
        Assert.Equal("plan-20260906-03", plan.Drafts[2].ExternalId);
        Assert.All(plan.Drafts, d => Assert.Equal("codeybox-self", d.ProjectId));
        Assert.All(plan.Drafts, d => Assert.Null(d.Agent));
        Assert.All(plan.Drafts, d => Assert.Null(d.Priority));
    }

    [Fact]
    public void A_plan_that_says_nothing_about_order_is_read_as_a_straight_line()
    {
        var plan = Composer.Parse(MarkdownPlan, "codeybox-self", "plan");

        Assert.Empty(plan.Drafts[0].DependsOn);
        Assert.Equal(["plan-01"], plan.Drafts[1].DependsOn);
        Assert.Equal(["plan-06"], plan.Drafts[6].DependsOn);
    }

    [Fact]
    public void The_prompt_is_the_whole_section_and_the_title_only_its_first_line()
    {
        var plan = Composer.Parse(MarkdownPlan, "codeybox-self", "plan");

        Assert.Contains("No test may reach the network.", plan.Drafts[2].Prompt, StringComparison.Ordinal);
        Assert.StartsWith("## Test selection (RTS) 3/7", plan.Drafts[2].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", plan.Drafts[2].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numbered_plan_splits_on_its_numbering_and_loses_it_from_the_titles()
    {
        var plan = Composer.Parse(
            """
            1. Inventory the suites
            Walk every csproj.

            2) Build the map
            Static analysis.

            Step 3: Enforce Layer 0
            No network.
            """,
            "codeybox-self",
            "plan");

        Assert.Equal(3, plan.Drafts.Count);
        Assert.Equal(["Inventory the suites", "Build the map", "Enforce Layer 0"], plan.Drafts.Select(d => d.Title));
        Assert.Equal(["plan-02"], plan.Drafts[2].DependsOn);
    }

    [Fact]
    public void A_numbered_list_inside_a_paragraph_is_a_list_not_a_plan()
    {
        // Splitting here would produce work items the operator has to delete, which is worse than not
        // splitting at all.
        var plan = Composer.Parse(
            """
            The audit keeps failing on the same file. We tried a few things.
            1. raising the iteration cap, which did nothing
            2. rerunning it, which did nothing either
            """,
            "codeybox-self",
            "plan");

        Assert.Single(plan.Drafts);
        Assert.Equal("1 item", plan.Summary);
    }

    [Fact]
    public void One_section_is_one_item_with_no_external_id_and_no_edges()
    {
        var plan = Composer.Parse("Refactor the audit view so the verdict is readable.", "codeybox-self", "plan");

        var draft = Assert.Single(plan.Drafts);
        Assert.False(plan.IsChain);
        Assert.Null(draft.ExternalId);
        Assert.Empty(draft.DependsOn);
        Assert.Equal("Refactor the audit view so the verdict is readable.", draft.Title);
        Assert.True(plan.IsReady);
    }

    [Fact]
    public void A_section_that_names_its_own_parents_overrides_the_straight_line()
    {
        var plan = Composer.Parse(
            """
            # Groundwork
            The shared types.

            # Left branch
            One half.

            # Right branch
            The other half.

            # Land it
            depends on: 1, 3
            Merge both halves.
            """,
            "codeybox-self",
            "plan");

        Assert.Equal(4, plan.Drafts.Count);
        Assert.Equal(["plan-01", "plan-03"], plan.Drafts[3].DependsOn);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void After_step_n_is_the_same_instruction_said_differently()
    {
        var plan = Composer.Parse(
            """
            # One
            a

            # Two
            b

            # Three
            after step 1
            c
            """,
            "codeybox-self",
            "plan");

        Assert.Equal(["plan-01"], plan.Drafts[2].DependsOn);
    }

    [Fact]
    public void A_reference_to_a_step_that_does_not_exist_is_reported_rather_than_dropped()
    {
        var plan = Composer.Parse(
            """
            # One
            a

            # Two
            depends on: 9
            b
            """,
            "codeybox-self",
            "plan");

        Assert.False(plan.IsReady);
        Assert.Contains("step 2 depends on step 9, which does not exist", plan.Problems);
    }

    [Fact]
    public void Steps_that_wait_on_each_other_are_caught_before_the_POST()
    {
        var plan = Composer.Parse(
            """
            # One
            depends on: 2

            # Two
            b
            """,
            "codeybox-self",
            "plan");

        Assert.Contains("the steps depend on each other in a cycle", plan.Problems);
        Assert.False(plan.IsReady);
    }

    [Fact]
    public void An_external_id_prefix_the_orchestrator_would_reject_is_reported_once_not_nine_times()
    {
        var reserved = Composer.Parse("# One\na\n\n# Two\nb", "codeybox-self", "wi-batch");
        Assert.Single(reserved.Problems);
        Assert.Contains("wi-", Assert.Single(reserved.Problems), StringComparison.Ordinal);

        var spaced = Composer.Parse("# One\na\n\n# Two\nb", "codeybox-self", "my plan");
        Assert.Single(spaced.Problems);
        Assert.Contains("must not contain a space", Assert.Single(spaced.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plan-01", true)]
    [InlineData("sprint-7:ticket-99", true)]
    [InlineData("wi-1", false)]
    [InlineData("has space", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff", false)]
    public void The_external_id_rules_are_the_orchestrators_own(string id, bool valid)
        => Assert.Equal(valid, Composer.ExternalIdProblem(id) is null);

    [Fact]
    public void A_horizontal_rule_splits_and_does_not_become_a_title()
    {
        var plan = Composer.Parse("First thing to do\nsome detail\n\n---\n\nSecond thing to do\nmore detail", "p", "plan");

        Assert.Equal(2, plan.Drafts.Count);
        Assert.Equal("Second thing to do", plan.Drafts[1].Title);
    }

    [Fact]
    public void Nothing_pasted_is_nothing_to_create()
    {
        var plan = Composer.Parse("   \n\n  ", "p", "plan");
        Assert.Empty(plan.Drafts);
        Assert.False(plan.IsReady);
        Assert.Equal("nothing to create", plan.Summary);
    }

    // -----------------------------------------------------------------------------------------------
    // Infer
    // -----------------------------------------------------------------------------------------------

    private static readonly Project[] Projects =
    [
        new("codeybox-self", "CodeyBox", null, "main", "claude"),
        new("other", "Other", null, "main", "codex"),
    ];

    [Fact]
    public void A_follow_up_knows_what_it_waits_on_and_nothing_else()
    {
        var from = Item("src", priority: 7);
        var draft = Composer.Infer(new ComposerContext(from, ComposerIntent.FollowUp, null, null), Projects, [from]);

        Assert.Equal(["src"], draft.DependsOn);
        Assert.Equal(string.Empty, draft.Title);
        Assert.Equal(string.Empty, draft.Prompt);
        Assert.Equal("codeybox-self", draft.ProjectId);
        Assert.Equal(7, draft.Priority);
        Assert.Null(draft.Agent);
        Assert.Null(draft.BaseBranch);
    }

    [Fact]
    public void A_sibling_joins_the_chain_beside_its_source_not_after_it()
    {
        var from = Item("src", dependsOn: ["parent-1", "parent-2"]);
        var draft = Composer.Infer(new ComposerContext(from, ComposerIntent.Sibling, null, null), Projects, [from]);

        Assert.Equal(["parent-1", "parent-2"], draft.DependsOn);
        Assert.Null(draft.Priority);
    }

    [Fact]
    public void A_split_carries_the_prompt_over_for_the_parser_and_keeps_the_original_parents()
    {
        var from = Item("src", prompt: "1. do this\n2. then that", dependsOn: ["parent"], priority: 3);
        var draft = Composer.Infer(new ComposerContext(from, ComposerIntent.Split, null, null), Projects, [from]);

        Assert.Equal("1. do this\n2. then that", draft.Prompt);
        Assert.Equal(["parent"], draft.DependsOn);
        Assert.Equal(3, draft.Priority);
    }

    [Fact]
    public void A_duplicate_copies_the_words_and_none_of_the_graph()
    {
        var from = Item("src", title: "Port the parser", prompt: "the whole brief", dependsOn: ["parent"], priority: 9);
        var draft = Composer.Infer(new ComposerContext(from, ComposerIntent.Duplicate, null, null), Projects, [from]);

        Assert.Equal("Port the parser", draft.Title);
        Assert.Equal("the whole brief", draft.Prompt);
        Assert.Empty(draft.DependsOn);
        Assert.Null(draft.Priority);
    }

    [Fact]
    public void A_promoted_suggestion_seeds_the_prompt_and_does_not_depend_on_where_it_came_from()
    {
        // The source work item is Done. An edge to it would be satisfied the moment it was written: it
        // is provenance, not a prerequisite.
        var suggestion = new Suggestion(
            Id: "s1",
            SourceWorkItemId: "wi-done",
            ProjectId: "other",
            Title: "Split the audit view",
            Rationale: "It renders four unrelated things.",
            Category: null,
            Severity: "important",
            EstimatedEffort: null,
            CreatedAt: Now,
            State: "open",
            PromotedToWorkItemId: null,
            FilesReferenced: ["AuditView.cs", "DiffView.cs"]);

        var draft = Composer.Infer(new ComposerContext(null, ComposerIntent.Promote, null, suggestion), Projects, []);

        Assert.Equal("Split the audit view", draft.Title);
        // The title leads the prompt: the agent reads the prompt, and the composer derives its title from
        // the prompt's first line, so the two agree by construction.
        Assert.Equal("Split the audit view\n\nIt renders four unrelated things.\n\nFiles: AuditView.cs, DiffView.cs", draft.Prompt);
        Assert.Empty(draft.DependsOn);
        Assert.Equal("other", draft.ProjectId);
    }

    [Fact]
    public void A_blank_composer_still_guesses_the_project_from_the_board()
    {
        var filtered = Composer.Infer(new ComposerContext(null, ComposerIntent.Blank, "other", null), Projects, []);
        Assert.Equal("other", filtered.ProjectId);
        Assert.Equal(string.Empty, filtered.Title);
        Assert.Empty(filtered.DependsOn);

        var unfiltered = Composer.Infer(new ComposerContext(null, ComposerIntent.Blank, null, null), Projects, []);
        Assert.Equal("codeybox-self", unfiltered.ProjectId);
    }

    // -----------------------------------------------------------------------------------------------
    // PriorityFor
    // -----------------------------------------------------------------------------------------------

    private static IReadOnlyList<Chain> Queue(params (string Id, int Priority)[] items)
        => BoardModel.Build(
            [.. items.Select((x, i) => Item(x.Id, priority: x.Priority) with { CreatedAt = Now.AddMinutes(-100 + i) })],
            Projects,
            0,
            2,
            Now).Next;

    [Fact]
    public void Next_means_one_above_whatever_is_currently_first()
    {
        Assert.Equal(11, Composer.PriorityFor(Position.Next, null, Queue(("a", 10), ("b", 3)), 1000));
        Assert.Equal(0, Composer.PriorityFor(Position.Next, null, [], 1000));
    }

    [Fact]
    public void At_the_cap_Next_returns_the_cap_rather_than_an_illegal_value()
    {
        // Not enough on its own, and deliberately so: past a ceiling the only way up is to move
        // everything else down, which is Reorder's job and not this function's.
        Assert.Equal(5, Composer.PriorityFor(Position.Next, null, Queue(("a", 5)), 5));
        Assert.Equal(1000, Composer.PriorityFor(Position.Next, null, Queue(("a", 1000)), 1000));
    }

    [Fact]
    public void After_a_chain_means_its_priority_because_created_at_breaks_the_tie_the_right_way()
    {
        var queue = Queue(("a", 10), ("b", 3));
        Assert.Equal(3, Composer.PriorityFor(Position.After, "b", queue, 1000));
        Assert.Equal(0, Composer.PriorityFor(Position.After, "missing", queue, 1000));
    }

    [Fact]
    public void Normal_is_the_default_and_Background_is_below_it_without_being_at_the_floor()
    {
        Assert.Equal(0, Composer.PriorityFor(Position.Normal, null, [], 1000));
        Assert.Equal(-100, Composer.PriorityFor(Position.Background, null, [], 1000));
    }
}
