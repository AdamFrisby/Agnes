using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Choosing what work waits on, from the queue rather than from memory.
/// </summary>
/// <remarks>
/// The form this replaces asked for <c>dependsOn</c> as comma-separated UUIDs. Everything below is a rule
/// that makes the list short enough to read: the answer is nearly always near the item being looked at, a
/// Done parent is a no-op, a Cancelled one is a trap, and an edge back into your own descendants is a
/// cycle the orchestrator will reject after you have already committed to it.
/// </remarks>
public class DependencyPickerTests
{
    private static IReadOnlyList<WorkItemRow> Queue() =>
    [
        Fake.Row("aaaa1111", title: "Chain root", ageDays: 9),
        Fake.Row("bbbb2222", title: "Chain middle", dependsOn: ["aaaa1111"], ageDays: 8),
        Fake.Row("cccc3333", title: "Chain leaf", dependsOn: ["bbbb2222"], ageDays: 7),
        Fake.Row("dddd4444", title: "Same project, unrelated", ageDays: 3),
        Fake.Row("eeee5555", title: "Same project, newer", ageDays: 1),
        Fake.Row("ffff6666", title: "Another project", project: "jobtrack-cli", ageDays: 2),
    ];

    [Fact]
    public void Chain_mates_come_first_then_the_project_then_everything_else()
    {
        // Band before recency. Sorting by time alone buries the chain-mate that is the answer far more
        // often than a stranger created an hour later.
        var picker = new DependencyPicker();

        picker.Reset(Queue().First(r => r.Id == "bbbb2222"), Queue());

        var bands = picker.Candidates
            .Select(c => c.SameChain ? 0 : c.SameProject ? 1 : 2)
            .ToList();

        Assert.Equal([.. bands.Order()], bands);
        Assert.Equal("aaaa1111", picker.Candidates[0].Item.Id);
    }

    [Fact]
    public void Inside_a_band_the_newest_is_offered_first()
    {
        var picker = new DependencyPicker();

        picker.Reset(Queue().First(r => r.Id == "dddd4444"), Queue());

        var sameProject = picker.Candidates.Where(c => c.SameProject && !c.SameChain).ToList();
        Assert.Equal(
            [.. sameProject.OrderByDescending(c => c.Item.CreatedAt).Select(c => c.Item.Id)],
            sameProject.Select(c => c.Item.Id));
    }

    [Fact]
    public void The_subject_and_everything_downstream_of_it_are_excluded()
    {
        // An edge back to something that already waits on you is a cycle. Excluding it outright beats
        // warning about it after the operator has ticked it.
        var picker = new DependencyPicker();

        picker.Reset(Queue().First(r => r.Id == "aaaa1111"), Queue());

        var offered = picker.Candidates.Select(c => c.Item.Id).ToList();
        Assert.DoesNotContain("aaaa1111", offered);
        Assert.DoesNotContain("bbbb2222", offered);   // direct child
        Assert.DoesNotContain("cccc3333", offered);   // grandchild
        Assert.Contains("dddd4444", offered);
    }

    [Fact]
    public void Descendants_are_followed_through_external_ids_as_well_as_uuids()
    {
        // dependsOn may name a parent by UUID, by externalId, or as "<project>:<externalId>". A walk that
        // only understood UUIDs would offer half a chain's own children back as candidates.
        IReadOnlyList<WorkItemRow> queue =
        [
            Fake.Row("aaaa1111", externalId: "plan-1-step-1"),
            Fake.Row("bbbb2222", dependsOn: ["plan-1-step-1"]),
        ];
        var picker = new DependencyPicker();

        picker.Reset(queue[0], queue);

        Assert.Empty(picker.Candidates);
    }

    [Fact]
    public void Done_and_cancelled_are_not_offered_but_failed_is()
    {
        // A Done parent is already satisfied and adding it changes nothing; a Cancelled one blocks its
        // children forever. A Failed one is the single most useful edge to be able to draw, because
        // retrying it is what unblocks the chain.
        IReadOnlyList<WorkItemRow> queue =
        [
            Fake.Row("done0001", "Done"),
            Fake.Row("canc0002", "Cancelled"),
            Fake.Row("fail0003", "Failed"),
            Fake.Row("work0004", "Working"),
            Fake.Row("subj0005"),
        ];
        var picker = new DependencyPicker();

        picker.Reset(queue[^1], queue);

        var offered = picker.Candidates.Select(c => c.Item.Id).ToList();
        Assert.DoesNotContain("done0001", offered);
        Assert.DoesNotContain("canc0002", offered);
        Assert.Contains("fail0003", offered);
        Assert.Contains("work0004", offered);
    }

    [Fact]
    public void Ticking_is_a_toggle_and_survives_a_search()
    {
        // The ticks are held apart from the rows on purpose: re-filtering rebuilds the visible list, and a
        // tick that scrolled out of view must not quietly come back untick.
        var picker = new DependencyPicker();
        picker.Reset(Queue().First(r => r.Id == "eeee5555"), Queue());

        picker.TickCommand.Execute(picker.Candidates.First(c => c.Item.Id == "aaaa1111"));
        Assert.Equal(1, picker.TickedCount);
        Assert.True(picker.HasTicked);

        picker.Search = "unrelated";
        Assert.DoesNotContain(picker.Candidates, c => c.Item.Id == "aaaa1111");
        Assert.Equal(1, picker.TickedCount);
        Assert.Contains("aaaa1111", picker.Ticked);

        picker.Search = string.Empty;
        picker.TickCommand.Execute(picker.Candidates.First(c => c.Item.Id == "aaaa1111"));
        Assert.Equal(0, picker.TickedCount);
    }

    [Fact]
    public void A_ticked_row_says_so()
    {
        var picker = new DependencyPicker();
        picker.Reset(Queue().First(r => r.Id == "eeee5555"), Queue());

        picker.TickCommand.Execute(picker.Candidates.First(c => c.Item.Id == "aaaa1111"));

        Assert.True(picker.Candidates.First(c => c.Item.Id == "aaaa1111").Ticked);
    }

    [Fact]
    public void Search_matches_the_handles_a_person_has_on_an_item()
    {
        var picker = new DependencyPicker();
        picker.Reset(null, Queue());

        picker.Search = "leaf";
        Assert.Equal("cccc3333", Assert.Single(picker.Candidates).Item.Id);

        picker.Search = "feat/dddd4444";   // the work branch
        Assert.Equal("dddd4444", Assert.Single(picker.Candidates).Item.Id);
    }

    [Fact]
    public void Reset_pre_ticks_what_was_asked_for()
    {
        var picker = new DependencyPicker();

        picker.Reset(Queue().First(r => r.Id == "cccc3333"), Queue(), ["aaaa1111", "bbbb2222"]);

        Assert.Equal(2, picker.TickedCount);
        Assert.True(picker.Candidates.First(c => c.Item.Id == "aaaa1111").Ticked);
    }

    [Fact]
    public void With_no_subject_nothing_is_a_chain_mate_and_nothing_is_excluded()
    {
        // The composer opens on work that does not exist yet: it has no chain and no descendants, so
        // every eligible item is on offer and the bands collapse to one.
        var picker = new DependencyPicker();

        picker.Reset(null, Queue());

        Assert.Equal(6, picker.Candidates.Count);
        Assert.DoesNotContain(picker.Candidates, c => c.SameChain);
    }
}
