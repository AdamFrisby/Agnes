using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Narrowing a real queue down to what a person can act on.
/// </summary>
/// <remarks>
/// Shaped after the live instance this was designed against: 404 items, of which 322 were Done and 50
/// Cancelled, leaving 32 that anyone could do something about. Showing all of them by default buried the
/// actionable few under twelve times as much history, with no search, filter or grouping to recover them.
/// </remarks>
public class QueueViewTests
{
    private static CodeyBoxQueueViewModel New()
        => new(new CodeyBoxClient(new CodeyBoxOptions("http://codeybox.test", "k")),
               a => { a(); return Task.CompletedTask; });

    private static WorkItemRow Row(
        string id, string state, string project = "codeybox-self", string agent = "claude",
        int priority = 0, bool depsOk = true, decimal cost = 0, string title = "A work item")
        // Named, not positional: the record has grown twice and positional arguments broke silently both
        // times, in a way the compiler only caught because the types happened to differ.
        => new(id, title, state, agent, project, 0, DateTimeOffset.UtcNow, null,
               Priority: priority, CreatedAt: DateTimeOffset.UtcNow.AddDays(-2), DependsOnSatisfied: depsOk,
               WorkBranch: "feat/x", UsageTotal: cost > 0 ? new UsageTotal(cost, 0, 0) : null);

    [Theory]
    [InlineData("Queued", true)]
    [InlineData("Failed", true)]
    [InlineData("AuditFailed", true)]
    [InlineData("Done", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Working", false)]   // running is not waiting on anyone
    public void What_counts_as_needing_attention(string state, bool expected)
        => Assert.Equal(expected, Row("id", state).NeedsAttention);

    [Fact]
    public void An_unsatisfied_dependency_needs_attention_whatever_the_state_says()
    {
        // 82 of the 404 items on the live instance carry dependencies. An item sitting still because
        // something else has not landed is the case an operator most needs surfaced, and its own state
        // says only "Queued".
        Assert.True(Row("id", "Queued", depsOk: false).NeedsAttention);
        Assert.True(Row("id", "Queued", depsOk: false).IsBlockedByDependency);
    }

    [Fact]
    public void The_default_view_hides_nothing_and_narrows_nothing()
    {
        // The filter chips are gone: the runway's horizons ARE the narrowing, and they are derived from
        // the queue rather than chosen from a row of buttons. So a freshly-opened tab starts with no
        // search and no filter, and what limits the list is which horizon a chain belongs to.
        var vm = New();

        Assert.Equal(string.Empty, vm.Search);
        Assert.Null(vm.ProjectFilter);
        Assert.Null(vm.AgentFilter);
    }

    [Fact]
    public void Search_narrows_the_visible_queue()
    {
        // Retained from the filter-chip era, retargeted: searching still has to narrow, and it now has to
        // narrow the thing the board is built from.
        var vm = New();
        vm.Load([Row("aaaa1111", "Queued", title: "Fix quota detection"),
                 Row("bbbb2222", "Queued", title: "Circuit breaker")]);

        vm.Search = "quota";

        Assert.Single(vm.Items);
        Assert.Equal("aaaa1111", vm.Items[0].Id);
    }

    [Fact]
    public void Search_reaches_the_prompt_as_well_as_the_title()
    {
        // The median prompt on this instance is 2,726 characters and is frequently the only place a
        // distinguishing word appears at all — a search that could not see it could not find the item.
        var row = Row("aaaa1111", "Queued", title: "Step 3") with { Prompt = "rewrite the dispatcher" };

        Assert.True(CodeyBoxQueueViewModel.Matches(row, "dispatcher"));
        Assert.False(CodeyBoxQueueViewModel.Matches(row, "auditor"));
    }

    [Fact]
    public void The_summary_line_says_what_is_hidden()
    {
        // A filter must never narrow the list silently: the count states the slice against the whole.
        var vm = New();
        vm.Load([Row("a", "Queued")]);

        Assert.Contains("need attention", vm.ViewSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_row_summary_carries_what_a_scan_needs()
    {
        var row = Row("43c8ec28aa11", "Working", priority: 55, cost: 73.4984m);

        Assert.Contains("43c8ec28", row.Summary, StringComparison.Ordinal);   // the id people quote
        Assert.Contains("Working", row.Summary, StringComparison.Ordinal);
        Assert.Contains("claude", row.Summary, StringComparison.Ordinal);
        Assert.Contains("codeybox-self", row.Summary, StringComparison.Ordinal);
        Assert.Contains("p55", row.Summary, StringComparison.Ordinal);
        Assert.Contains("$73.50", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_cost_item_says_nothing_about_cost()
    {
        // Most items report no total; "$0.00" on each of them would be noise standing where a real
        // figure goes.
        Assert.Null(Row("a", "Queued").Cost);
        Assert.DoesNotContain("$", Row("a", "Queued").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_merged_item_carries_its_pull_request()
    {
        var merged = Row("a", "Done") with { MergedPrNumber = 412 };

        Assert.True(merged.HasPr);
        Assert.Equal("#412", merged.PrLabel);
        Assert.Contains("#412", merged.Summary, StringComparison.Ordinal);
    }
}
