using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The composer: what it infers, what it lets you override, and how a pasted plan becomes a chain.
/// </summary>
/// <remarks>
/// <para>The tests marked <c>Skip</c> are waiting on <c>Composer.Infer</c> / <c>Composer.Parse</c> /
/// <c>Composer.PriorityFor</c> in <c>BoardModel.cs</c>, which is landing alongside this and today throws
/// <see cref="NotImplementedException"/>. <b>Integrator: remove the Skip.</b> They are written against the
/// real functions rather than a stub, because what is worth pinning is that the composer shows what the
/// model inferred — a stub would only pin that it shows what the stub said.</para>
///
/// <para>Chain creation is tested without the parser, by handing the composer a <see cref="Plan"/>
/// directly: the ordering rule (parents first, each naming its parents' externalIds) and the
/// stop-on-first-failure reporting are this class's own behaviour, not the parser's.</para>
/// </remarks>
public class ComposerViewModelTests
{
    private const string PendingModel =
        "Composer's pure half is being implemented concurrently and throws NotImplementedException. Integrator: un-skip.";

    private static readonly Project[] TheProjects =
    [
        new("codeybox-self", "CodeyBox (self-modify)", null, "main", "codex", 25, ["security", "tests"]),
        new("jobtrack-cli", "JobTrack CLI", null, "master", "claude", 10, ["quality"], MaxPriority: 200),
    ];

    private static (ComposerViewModel Composer, RoutingHandler Http, List<string> Selected) New(
        IReadOnlyList<WorkItemRow>? items = null,
        IReadOnlyList<Chain>? next = null)
    {
        var http = new RoutingHandler();
        var selected = new List<string>();
        var composer = new ComposerViewModel(
            new CodeyBoxClient(new CodeyBoxOptions("http://codeybox.test", "k"), http),
            action => { action(); return Task.CompletedTask; },
            () => TheProjects,
            () => items ?? [],
            () => next ?? [],
            () => Task.CompletedTask,
            selected.Add);
        return (composer, http, selected);
    }

    private static Draft Draft(string title, string externalId, params string[] dependsOn)
        => new("codeybox-self", title, $"do {title}", externalId, dependsOn, null, null, null, null, null, false);

    // ---- opening ----

    [Fact]
    public void A_blank_composer_opens_on_the_filtered_project()
    {
        var (composer, _, _) = New();

        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "jobtrack-cli", null));

        Assert.True(composer.IsOpen);
        Assert.Equal("jobtrack-cli", composer.ProjectId);
        Assert.Equal(2, composer.Projects.Count);
    }

    [Fact]
    public void A_follow_up_opens_on_the_item_it_follows_and_pre_ticks_the_edge()
    {
        var from = Fake.Row("aaaa1111", title: "Fix quota detection", project: "jobtrack-cli");
        var (composer, _, _) = New([from, Fake.Row("bbbb2222", project: "jobtrack-cli")]);

        composer.Open(new ComposerContext(from, ComposerIntent.FollowUp, null, null));

        Assert.Equal(ComposerIntent.FollowUp, composer.Intent);
        Assert.Equal("jobtrack-cli", composer.ProjectId);
        Assert.Contains("aaaa1111", composer.Picker.Ticked);
    }

    [Fact]
    public void A_sibling_joins_the_chain_without_waiting_on_it()
    {
        // A sibling is another step at the same level, not a follow-up. Pre-ticking the edge would quietly
        // turn every "one more like this" into "one more after this".
        var from = Fake.Row("aaaa1111");
        var (composer, _, _) = New([from]);

        composer.Open(new ComposerContext(from, ComposerIntent.Sibling, null, null));

        Assert.Empty(composer.Picker.Ticked);
    }

    [Fact]
    public void A_duplicate_carries_the_prompt_across_and_depends_on_nothing()
    {
        var from = Fake.Row("aaaa1111", title: "Fix quota detection", prompt: "rewrite the dispatcher");
        var (composer, _, _) = New([from]);

        composer.Open(new ComposerContext(from, ComposerIntent.Duplicate, null, null));

        Assert.Equal("rewrite the dispatcher", composer.Text);
        Assert.Empty(composer.Picker.Ticked);
    }

    [Fact]
    public void Promoting_a_suggestion_seeds_the_prompt_from_its_rationale()
    {
        var suggestion = new Suggestion(
            "s1", "src", "codeybox-self", "Cache the audit reports", "they are re-read on every open",
            "performance", "important", "small", DateTimeOffset.UtcNow, "open", null, ["a.cs"]);
        var (composer, _, _) = New();

        composer.Open(new ComposerContext(null, ComposerIntent.Promote, "codeybox-self", suggestion));

        Assert.Contains("Cache the audit reports", composer.Text, StringComparison.Ordinal);
        Assert.Contains("re-read on every open", composer.Text, StringComparison.Ordinal);
        Assert.Equal("Cache the audit reports", composer.Title);
    }

    [Fact(Skip = PendingModel)]
    public void What_the_model_infers_is_what_the_composer_shows()
    {
        var from = Fake.Row("aaaa1111", agent: "codex", project: "codeybox-self");
        var (composer, _, _) = New([from]);
        var inferred = Composer.Infer(
            new ComposerContext(from, ComposerIntent.FollowUp, null, null), TheProjects, [from]);

        composer.Open(new ComposerContext(from, ComposerIntent.FollowUp, null, null));

        Assert.Equal(inferred.ProjectId, composer.ProjectId);
        Assert.Equal(inferred.Agent, composer.Agent);
        Assert.Equal(inferred.BaseBranch, composer.BaseBranch);
        Assert.Equal(inferred.AuditMaxIterations, composer.AuditMaxIterations);
    }

    // ---- inherited values are shown, not left blank ----

    [Fact]
    public void An_unset_override_states_the_project_default_rather_than_leaving_a_gap()
    {
        // An empty box reads as a gap; a chip saying "codex (project default)" reads as a decision. The
        // difference is whether the operator has to go and look the answer up somewhere else.
        var (composer, _, _) = New();

        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));

        Assert.True(composer.AgentIsInherited);
        Assert.Equal("codex (project default)", composer.AgentInherited);
        Assert.Equal("main (project default)", composer.BaseBranchInherited);
        Assert.Equal("25 (project default)", composer.AuditMaxIterationsInherited);

        composer.Agent = "claude";
        Assert.False(composer.AgentIsInherited);
    }

    [Fact]
    public void Choosing_a_project_narrows_the_auditor_profiles_to_the_ones_it_configures()
    {
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));
        composer.AuditorProfile = "security";

        composer.ProjectId = "jobtrack-cli";

        Assert.Equal(["quality"], composer.AuditorProfiles);
        Assert.True(composer.HasAuditorProfiles);

        // The profile that project does not offer is dropped rather than sent as something it will reject.
        Assert.Null(composer.AuditorProfile);
    }

    // ---- title derivation ----

    [Fact]
    public void The_title_follows_the_first_line_until_someone_types_over_it()
    {
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));

        composer.Text = "## Rewrite the dispatcher\n\nand then some detail";
        Assert.True(composer.TitleIsDerived);
        Assert.Equal("Rewrite the dispatcher", composer.Title);

        composer.Title = "Something else";
        Assert.False(composer.TitleIsDerived);

        composer.Text = "## A different first line\n\nmore";
        Assert.Equal("Something else", composer.Title);
    }

    [Fact]
    public void A_derived_title_skips_blank_and_decorated_leading_lines()
        => Assert.Equal("Rewrite the dispatcher",
                        ComposerViewModel.FirstLine("\n\n  ### Rewrite the dispatcher  \nbody"));

    // ---- position and priority ----

    [Fact]
    public void The_position_words_and_the_number_are_both_shown()
    {
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));

        Assert.Equal(Position.Normal, composer.Position);
        Assert.Equal(4, composer.Positions.Count);
        Assert.False(composer.PriorityIsManual);
    }

    [Fact]
    public void A_typed_priority_is_never_silently_overwritten_by_a_position()
    {
        // The operator on the live instance steers pickup with ~70 distinct priority values. A words-only
        // control would take that away; a words control that resets what they typed would be worse.
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));

        composer.Priority = 137;
        Assert.True(composer.PriorityIsManual);

        composer.Position = Position.Background;
        Assert.Equal(137, composer.Priority);
    }

    [Fact(Skip = PendingModel)]
    public void A_position_maps_to_the_number_the_model_says()
    {
        var next = new[] { Fake.Chain(Fake.Row("aaaa1111", priority: 50)), Fake.Chain(Fake.Row("bbbb2222", priority: 20)) };
        var (composer, _, _) = New(next: next);
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));

        composer.Position = Position.Next;

        Assert.Equal(Composer.PriorityFor(Position.Next, null, next, 1000), composer.Priority);
        Assert.False(composer.PriorityIsManual);
    }

    // ---- creating ----

    [Fact]
    public async Task A_single_item_is_one_post_carrying_the_ticked_dependencies()
    {
        var (composer, http, selected) = New([Fake.Row("aaaa1111")]);
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));
        composer.Text = "Rewrite the dispatcher";
        composer.Priority = 40;
        composer.Picker.TickCommand.Execute(composer.Picker.Candidates.First(c => c.Item.Id == "aaaa1111"));

        http.Clear();
        http.CreatedIds.Enqueue("new00001");
        await composer.CreateCommand.ExecuteAsync(null);

        var post = Assert.Single(http.Requests, r => r is { Method: "POST", Path: "/workitems" });
        Assert.Contains("\"title\":\"Rewrite the dispatcher\"", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"priority\":40", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"dependsOn\":[\"aaaa1111\"]", post.Body, StringComparison.Ordinal);
        Assert.Equal(["new00001"], selected);
        Assert.False(composer.IsOpen);
    }

    [Fact]
    public async Task A_chain_is_created_parents_first_with_each_child_naming_its_parents()
    {
        // POST /workitems accepts a dependsOn naming an externalId only once that item ALREADY exists, so
        // the order is not cosmetic — a child sent before its parent is rejected outright.
        var (composer, http, selected) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));
        composer.Text = "a plan";
        composer.Plan = new Plan(
            [Draft("Third", "p-3", "p-2"), Draft("First", "p-1"), Draft("Second", "p-2", "p-1")],
            [],
            "p");

        http.Clear();
        foreach (var id in new[] { "id000001", "id000002", "id000003" })
        {
            http.CreatedIds.Enqueue(id);
        }

        await composer.CreateCommand.ExecuteAsync(null);

        var posts = http.Requests.Where(r => r is { Method: "POST", Path: "/workitems" }).ToList();
        Assert.Equal(3, posts.Count);
        Assert.Contains("\"title\":\"First\"", posts[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"Second\"", posts[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"Third\"", posts[2].Body, StringComparison.Ordinal);

        // Each draft carries its own externalId, and a child names its parent by the id it came back with.
        Assert.Contains("\"externalId\":\"p-1\"", posts[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"dependsOn\":[\"id000001\"]", posts[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"dependsOn\":[\"id000002\"]", posts[2].Body, StringComparison.Ordinal);

        Assert.Equal("id000001", selected[0]);
    }

    [Fact]
    public async Task A_chain_stops_at_the_first_failure_and_says_what_landed()
    {
        // There is no batch endpoint and no transaction, so a half-created chain is a real thing that
        // exists. Reporting it precisely is the only honest option: the steps that landed are queued, and
        // the operator's next move is to look at the one that did not.
        var (composer, http, selected) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));
        composer.Text = "a plan";
        composer.Plan = new Plan(
            [Draft("First", "p-1"), Draft("Second", "p-2", "p-1"), Draft("Third", "p-3", "p-2")], [], "p");

        http.Clear();
        http.CreatedIds.Enqueue("id000001");
        http.CreatesBeforeFailing = 1;

        await composer.CreateCommand.ExecuteAsync(null);

        Assert.Contains("Created 1 of 3", composer.Status, StringComparison.Ordinal);
        Assert.Contains("Second", composer.Status, StringComparison.Ordinal);

        // Only the failing step was attempted after the one that landed — the rest are not sent into a
        // queue whose parent does not exist.
        Assert.Equal(2, http.Requests.Count(r => r is { Method: "POST", Path: "/workitems" }));

        // Still open, holding the plan, so the operator can see what happened.
        Assert.True(composer.IsOpen);
        Assert.Empty(selected);
    }

    [Fact]
    public void Cancelling_clears_the_form()
    {
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));
        composer.Text = "half a thought";

        composer.CancelCommand.Execute(null);

        Assert.False(composer.IsOpen);
        Assert.Equal(string.Empty, composer.Text);
        Assert.Equal(string.Empty, composer.Title);
        Assert.True(composer.TitleIsDerived);
    }

    [Fact]
    public void Nothing_can_be_created_without_a_project_a_title_and_a_prompt()
    {
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, null, null));
        composer.ProjectId = null;
        Assert.False(composer.CanCreate);

        composer.ProjectId = "codeybox-self";
        Assert.False(composer.CanCreate);

        composer.Text = "do the thing";
        Assert.True(composer.CanCreate);
    }

    [Fact]
    public void A_plan_with_a_problem_cannot_be_created()
    {
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));
        composer.Text = "a plan";
        composer.Plan = new Plan([Draft("First", "p-1")], ["Step 1 needs a title and a prompt."], "p");

        Assert.True(composer.HasProblems);
        Assert.False(composer.CanCreate);
    }

    // ---- editing the plan's edges ----

    [Fact]
    public void Reticking_an_edge_edits_the_plan_rather_than_re_parsing_it()
    {
        // The text has not changed, so re-splitting it would throw the operator's edges away.
        var (composer, _, _) = New();
        composer.Open(new ComposerContext(null, ComposerIntent.Blank, "codeybox-self", null));
        composer.Text = "a plan";
        composer.Plan = new Plan([Draft("First", "p-1"), Draft("Second", "p-2", "p-1")], [], "p");

        composer.ToggleParent(new DraftEdge(1, 0, true));

        Assert.Empty(composer.Plan!.Drafts[1].DependsOn);

        composer.ToggleParent(new DraftEdge(1, 0, false));
        Assert.Equal(["p-1"], composer.Plan!.Drafts[1].DependsOn);
    }

    [Fact]
    public void A_loop_in_the_drafts_is_reported_as_a_problem()
    {
        // None of these items exist yet, so BoardModel.WouldCycle — which walks the live queue — has
        // nothing to walk. The check is over the draft indices instead.
        var problems = ComposerViewModel.Validate(
            [Draft("First", "p-1", "p-2"), Draft("Second", "p-2", "p-1")]);

        Assert.Contains(problems, p => p.Contains("loop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Topological_order_is_stable_for_a_plan_that_is_already_in_order()
        => Assert.Equal(
            ["First", "Second", "Third"],
            ComposerViewModel.TopologicalOrder(
                [Draft("First", "p-1"), Draft("Second", "p-2", "p-1"), Draft("Third", "p-3", "p-2")])
                .Select(d => d.Title));

    [Fact]
    public void A_partial_report_names_the_step_that_stopped_it()
    {
        Assert.Contains("Nothing was created", ComposerViewModel.Partial(0, 3, "First", "boom"), StringComparison.Ordinal);
        Assert.Contains("Created 2 of 3", ComposerViewModel.Partial(2, 3, "Third", "boom"), StringComparison.Ordinal);
        Assert.Contains("Third", ComposerViewModel.Partial(2, 3, "Third", "boom"), StringComparison.Ordinal);
    }
}
