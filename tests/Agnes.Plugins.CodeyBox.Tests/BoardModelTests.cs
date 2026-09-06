using System.Diagnostics;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The runway, against the shapes a real queue holds: chains filed in batches, a numbered series with no
/// edges at all, work parked by the orchestrator, and a landed tail that has to fall off the board.
/// Every assertion here is about what an operator reads, not about the intermediate structure.
/// </summary>
public sealed class BoardModelTests
{
    // Local time, because everything the board says about a clock is said in the reader's own.
    private static DateTimeOffset Local(int day, int hour, int minute)
        => new(new DateTime(2026, 9, day, hour, minute, 0, DateTimeKind.Local));

    private static readonly DateTimeOffset Now = Local(6, 15, 0);

    private static WorkItemRow Item(
        string id,
        string state = "Queued",
        string? title = null,
        string[]? dependsOn = null,
        bool satisfied = true,
        int priority = 0,
        string? project = "codeybox-self",
        string? agent = "claude",
        int createdMinutesAgo = 600,
        int updatedMinutesAgo = 60,
        DateTimeOffset? quotaRetry = null)
        => new(
            Id: id,
            Title: title ?? id,
            State: state,
            Agent: agent,
            ProjectId: project,
            QueuePosition: 0,
            UpdatedAt: Now.AddMinutes(-updatedMinutesAgo),
            LastError: null,
            DependsOn: dependsOn,
            Priority: priority,
            CreatedAt: Now.AddMinutes(-createdMinutesAgo),
            DependsOnSatisfied: satisfied,
            NextQuotaRetryAt: quotaRetry);

    private static Board Build(params WorkItemRow[] items) => BoardModel.Build(items, [], 0, 2, Now);

    // -----------------------------------------------------------------------------------------------
    // Chains
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_chain_is_a_connected_component_not_a_single_edge()
    {
        // c depends on b depends on a, and d on nothing: two chains, not four items.
        var chains = BoardModel.Chains(
        [
            Item("a", createdMinutesAgo: 40),
            Item("b", dependsOn: ["a"], createdMinutesAgo: 30),
            Item("c", dependsOn: ["b"], createdMinutesAgo: 20),
            Item("d", createdMinutesAgo: 10),
        ]);

        Assert.Equal(2, chains.Count);
        Assert.Contains(chains, c => c.Steps.Count == 3 && c.Id == "a");
        Assert.Contains(chains, c => c.Steps.Count == 1 && c.Id == "d");
    }

    [Fact]
    public void A_numbered_series_joins_a_chain_with_no_edges_at_all()
    {
        // The batch this was written for: nine items filed in one second, numbered in their titles, and
        // not one dependency between them. Reading them as nine unrelated rows is what the board did.
        var chains = BoardModel.Chains(
        [
            Item("s1", title: "Test selection (RTS) 1/3: inventory", createdMinutesAgo: 30),
            Item("s2", title: "Test selection (RTS) 2/3: pick the graph", createdMinutesAgo: 29),
            Item("s3", title: "Test selection (RTS) 3/3: enforce Layer 0", createdMinutesAgo: 28),
        ]);

        var chain = Assert.Single(chains);
        Assert.Equal(3, chain.Steps.Count);
        Assert.Equal("Test selection (RTS)", chain.Title);
        Assert.Equal(["1/3", "2/3", "3/3"], chain.Steps.Select(s => s.Series));
    }

    [Fact]
    public void A_series_does_not_reach_across_projects_or_across_a_different_total()
    {
        var chains = BoardModel.Chains(
        [
            Item("a", title: "Port the parser 1/2: lexer", project: "one"),
            Item("b", title: "Port the parser 2/2: parser", project: "two"),
            Item("c", title: "Port the parser 1/4: lexer", project: "one"),
        ]);

        Assert.Equal(3, chains.Count);
    }

    [Fact]
    public void A_diamond_orders_topologically_and_names_itself_after_its_root()
    {
        // b and c both wait on a; d waits on both. Any order that puts a child before its parent would
        // make "step 3 of 4" a lie.
        var chain = Assert.Single(BoardModel.Chains(
        [
            Item("d", dependsOn: ["b", "c"], createdMinutesAgo: 10, title: "land it"),
            Item("b", dependsOn: ["a"], createdMinutesAgo: 30, title: "left"),
            Item("c", dependsOn: ["a"], createdMinutesAgo: 20, title: "right"),
            Item("a", createdMinutesAgo: 40, title: "groundwork"),
        ]));

        Assert.Equal(["a", "b", "c", "d"], chain.Steps.Select(s => s.Item.Id));
        Assert.Equal("a", chain.Id);
        Assert.Equal("groundwork", chain.Title);
    }

    [Fact]
    public void An_id_that_is_not_in_the_list_is_not_an_edge()
    {
        // A filtered board must not invent a chain out of a parent it cannot see.
        var chains = BoardModel.Chains([Item("a", dependsOn: ["gone"]), Item("b", dependsOn: ["also-gone"])]);
        Assert.Equal(2, chains.Count);
    }

    [Theory]
    [InlineData("Done", StepState.Done)]
    [InlineData("Working", StepState.Running)]
    [InlineData("Auditing", StepState.Running)]
    [InlineData("WorkComplete", StepState.Running)]
    [InlineData("PlanReview", StepState.Running)]
    [InlineData("UpstreamPushing", StepState.Running)]
    [InlineData("WaitingForQuotaReset", StepState.Parked)]
    [InlineData("WaitingForTransientRetry", StepState.Parked)]
    [InlineData("NeedsOperatorInput", StepState.NeedsPerson)]
    [InlineData("AuditFailed", StepState.Failed)]
    [InlineData("Cancelled", StepState.Cancelled)]
    public void Every_state_the_orchestrator_reports_reads_as_one_pip(string state, StepState expected)
        => Assert.Equal(expected, BoardModel.StateOf(Item("x", state)));

    [Fact]
    public void Queued_is_the_one_state_a_dependency_can_tell_apart()
    {
        Assert.Equal(StepState.Ready, BoardModel.StateOf(Item("x", "Queued")));
        Assert.Equal(StepState.Blocked, BoardModel.StateOf(Item("x", "Queued", satisfied: false)));
    }

    // -----------------------------------------------------------------------------------------------
    // Head
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_head_is_what_is_running_then_what_can_start_then_what_stops_it()
    {
        var running = Assert.Single(BoardModel.Chains(
            [Item("a", "Done", createdMinutesAgo: 30), Item("b", "Working", dependsOn: ["a"], createdMinutesAgo: 20)]));
        Assert.Equal("b", BoardModel.PickHead(running.Steps).Item.Id);

        var ready = Assert.Single(BoardModel.Chains(
            [Item("a", "Done", createdMinutesAgo: 30), Item("b", dependsOn: ["a"], createdMinutesAgo: 20)]));
        Assert.Equal("b", BoardModel.PickHead(ready.Steps).Item.Id);

        var stuck = Assert.Single(BoardModel.Chains(
            [Item("a", "Failed", createdMinutesAgo: 30), Item("b", dependsOn: ["a"], satisfied: false, createdMinutesAgo: 20)]));
        Assert.Equal("a", BoardModel.PickHead(stuck.Steps).Item.Id);

        var finished = Assert.Single(BoardModel.Chains(
            [Item("a", "Done", createdMinutesAgo: 30), Item("b", "Done", dependsOn: ["a"], createdMinutesAgo: 20)]));
        Assert.Equal("b", BoardModel.PickHead(finished.Steps).Item.Id);
    }

    [Fact]
    public void A_failed_ancestor_wins_over_one_that_is_merely_in_flight()
    {
        // An in-flight parent needs nothing from anybody; naming it would tell the operator to wait for
        // something that is never going to arrive.
        var chain = Assert.Single(BoardModel.Chains(
        [
            Item("run", "Working", createdMinutesAgo: 40),
            Item("dead", "Cancelled", createdMinutesAgo: 39),
            Item("child", dependsOn: ["run", "dead"], satisfied: false, createdMinutesAgo: 10),
        ]));

        var head = chain.Steps.First(s => s.Item.Id == "child");
        Assert.Equal("dead", BoardModel.BlockerFor(chain, head)?.Item.Id);
    }

    // -----------------------------------------------------------------------------------------------
    // Horizons and their Why lines
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_running_step_says_where_it_is_in_the_chain_and_who_is_on_it()
    {
        var board = Build(
            Item("s1", "Done", title: "Test selection 1/3: inventory", createdMinutesAgo: 30),
            Item("s2", "Working", title: "Test selection 2/3: pick", createdMinutesAgo: 29),
            Item("s3", title: "Test selection 3/3: enforce", createdMinutesAgo: 28, satisfied: false));

        var row = Assert.Single(board.Now);
        Assert.Equal("step 2 of 3 running on claude", row.Why);
        Assert.Equal("s2", row.Head.Id);
        Assert.Equal(Horizon.Now, row.Horizon);
    }

    [Fact]
    public void A_lone_running_item_does_not_pretend_to_be_a_chain()
    {
        var row = Assert.Single(Build(Item("only", "Auditing")).Now);
        Assert.Equal("running on claude", row.Why);
    }

    [Fact]
    public void A_chain_holding_two_slots_takes_two_rows_in_Now()
    {
        // Now is a picture of the slots in use. One row for a chain occupying two of them would
        // contradict the header above it.
        var board = Build(
            Item("s1", "Working", title: "Port 1/2: lexer", createdMinutesAgo: 30, updatedMinutesAgo: 20),
            Item("s2", "Auditing", title: "Port 2/2: parser", createdMinutesAgo: 29, updatedMinutesAgo: 5));

        Assert.Equal(2, board.Now.Count);
        Assert.Equal(["s2", "s1"], board.Now.Select(r => r.Head.Id));  // most recently touched first
        Assert.Equal("step 1 of 2 running on claude", board.Now[1].Why);
    }

    [Fact]
    public void Next_is_the_dispatch_order_and_says_the_position_in_words()
    {
        var board = Build(
            Item("mid", priority: 5, createdMinutesAgo: 100),
            Item("top", priority: 10, createdMinutesAgo: 50),
            Item("late", priority: 5, createdMinutesAgo: 20));

        Assert.Equal(["top", "mid", "late"], board.Next.Select(c => c.Head.Id));
        Assert.Equal([0, 1, 2], board.Next.Select(c => c.DispatchRank));
        Assert.Equal(["next up", "2nd in line", "3rd in line"], board.Next.Select(c => c.Why));
        Assert.All(board.Next, c => Assert.Equal(WaitReason.Slot, c.Reason));
    }

    [Theory]
    [InlineData(1, "next up")]
    [InlineData(3, "3rd in line")]
    [InlineData(11, "11th in line")]
    [InlineData(12, "12th in line")]
    [InlineData(21, "21st in line")]
    public void Positions_past_tenth_are_still_read_as_ordinals(int position, string expected)
        => Assert.Equal(expected, BoardModel.WhyNext(position - 1));

    [Fact]
    public void A_chain_held_up_by_a_cancelled_parent_names_it()
    {
        var board = Build(
            Item("parent", "Cancelled", createdMinutesAgo: 40),
            Item("child", dependsOn: ["parent"], satisfied: false, createdMinutesAgo: 30));

        var group = Assert.Single(board.Waiting);
        var row = Assert.Single(group.Chains);
        Assert.Equal(WaitReason.Parent, row.Reason);
        Assert.Equal("child", row.Head.Id);
        Assert.Equal("parent", row.Blocker?.Item.Id);
        Assert.Equal("waiting on step 1, which was cancelled", row.Why);
        Assert.Equal("Waiting on a parent", group.Title);
    }

    [Fact]
    public void A_parked_item_says_when_it_comes_back()
    {
        // Stopped but not stuck: the orchestrator resumes this one by itself, and saying so is what
        // stops an operator retrying something that was already going to retry.
        var board = Build(Item("q", "WaitingForQuotaReset", quotaRetry: Local(6, 16, 30)));
        var row = Assert.Single(Assert.Single(board.Waiting).Chains);

        Assert.Equal(WaitReason.Quota, row.Reason);
        Assert.Equal("parked until 16:30", row.Why);
    }

    [Fact]
    public void A_parked_item_with_no_resume_time_still_says_what_it_is_waiting_for()
    {
        var row = Assert.Single(Assert.Single(Build(Item("q", "WaitingForAgentResume")).Waiting).Chains);
        Assert.Equal("waiting for quota", row.Why);
    }

    [Fact]
    public void The_two_things_that_need_a_person_are_told_apart()
    {
        var board = Build(
            Item("ask", "NeedsOperatorInput", updatedMinutesAgo: 5),
            Item("broke", "Failed", updatedMinutesAgo: 50));

        var group = Assert.Single(board.Waiting);
        Assert.Equal(WaitReason.Person, group.Reason);
        Assert.Equal("Needs you", group.Title);
        Assert.Equal(["needs your answer", "failed: needs a decision"], group.Chains.Select(c => c.Why));
    }

    [Fact]
    public void Waiting_groups_are_read_in_the_order_the_operator_can_act_on_them()
    {
        var board = Build(
            Item("q", "WaitingForQuotaReset"),
            Item("dead", "Cancelled", createdMinutesAgo: 40),
            Item("kid", dependsOn: ["dead"], satisfied: false, createdMinutesAgo: 30),
            Item("ask", "NeedsOperatorInput"));

        Assert.Equal(
            [WaitReason.Person, WaitReason.Parent, WaitReason.Quota],
            board.Waiting.Select(g => g.Reason));
        Assert.Equal(
            ["Needs you", "Waiting on a parent", "Parked by the orchestrator"],
            board.Waiting.Select(g => g.Title));
    }

    [Fact]
    public void Landed_work_is_grouped_by_the_day_it_landed()
    {
        var board = Build(
            Item("today", "Done", updatedMinutesAgo: (int)(Now - Local(6, 14, 2)).TotalMinutes),
            Item("yesterday", "Done", updatedMinutesAgo: (int)(Now - Local(5, 9, 30)).TotalMinutes),
            Item("friday", "Done", updatedMinutesAgo: (int)(Now - Local(4, 11, 0)).TotalMinutes));

        Assert.Equal(["Today", "Yesterday", "Fri 4 Sep"], board.Landed.Select(d => d.Title));
        Assert.Equal("landed 14:02", board.Landed[0].Chains[0].Why);
        Assert.Equal("landed Sat 09:30", board.Landed[1].Chains[0].Why);
        Assert.Equal(3, board.LandedCount);
    }

    [Fact]
    public void Work_older_than_the_window_falls_off_the_board_into_history()
    {
        var board = Build(
            Item("recent", "Done", updatedMinutesAgo: 60),
            Item("old", "Done", updatedMinutesAgo: 60 * 24 * (Board.LandedWindowDays + 1)),
            Item("gone", "Cancelled", updatedMinutesAgo: 30));

        Assert.Equal(["recent"], board.Landed.SelectMany(d => d.Chains).Select(c => c.Id));
        Assert.Equal(2, board.HistoryCount);
        Assert.Contains("2 older or cancelled items", board.HistoryLabel);
    }

    [Fact]
    public void A_chain_that_ended_half_cancelled_is_dated_by_whatever_happened_last()
    {
        var board = Build(
            Item("a", "Done", createdMinutesAgo: 90, updatedMinutesAgo: 80),
            Item("b", "Cancelled", dependsOn: ["a"], createdMinutesAgo: 89, updatedMinutesAgo: 30));

        var row = Assert.Single(board.Landed.SelectMany(d => d.Chains));
        Assert.Equal(Horizon.Landed, row.Horizon);
        Assert.Equal("1 of 2", row.Progress);
    }

    [Fact]
    public void A_chain_nobody_finished_is_history_and_says_so()
    {
        var board = Build(Item("x", "Cancelled", updatedMinutesAgo: 10));

        Assert.Empty(board.Landed);
        Assert.Equal(1, board.HistoryCount);
    }

    // -----------------------------------------------------------------------------------------------
    // Relations
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Relations_report_the_ancestor_whose_retry_would_unblock_this_one()
    {
        // Two levels up: the parent is in flight and needs nothing, so the answer is the grandparent.
        WorkItemRow[] items =
        [
            Item("root", "AuditFailed", createdMinutesAgo: 90),
            Item("mid", "Working", dependsOn: ["root"], createdMinutesAgo: 60),
            Item("leaf", dependsOn: ["mid"], satisfied: false, createdMinutesAgo: 30),
        ];

        var relations = BoardModel.RelationsOf(items[2], items, Now);

        Assert.Equal("root", relations.BlockingRoot?.Item.Id);
        Assert.True(relations.IsBlocked);
        Assert.Equal(["mid"], relations.Parents.Select(p => p.Item.Id));
        Assert.All(relations.Parents, p => Assert.False(p.Satisfied));
        Assert.Empty(relations.Children);
        Assert.Equal("step 3 of 3", relations.PositionLabel);
    }

    [Fact]
    public void Only_Done_satisfies_a_dependency_and_children_are_found_by_reverse_edge()
    {
        WorkItemRow[] items =
        [
            Item("a", "Done", createdMinutesAgo: 90),
            Item("b", dependsOn: ["a"], createdMinutesAgo: 60),
            Item("c", dependsOn: ["b"], satisfied: false, createdMinutesAgo: 30),
        ];

        var relations = BoardModel.RelationsOf(items[1], items, Now);

        Assert.True(Assert.Single(relations.Parents).Satisfied);
        Assert.Equal(["c"], relations.Children.Select(c => c.Item.Id));
        Assert.Null(relations.BlockingRoot);
    }

    [Fact]
    public void An_item_with_no_relations_still_gets_a_chain_of_its_own()
    {
        var lone = Item("lone");
        var relations = BoardModel.RelationsOf(lone, [lone], Now);

        Assert.True(relations.Chain.IsSingleton);
        Assert.Equal(string.Empty, relations.PositionLabel);
    }

    // -----------------------------------------------------------------------------------------------
    // Reorder. Every case is proved by re-running the dispatcher's own sort, because that sort is the
    // specification — arguing about the arithmetic proves nothing.
    // -----------------------------------------------------------------------------------------------

    private static IReadOnlyList<PriorityChange> Move(
        IReadOnlyList<WorkItemRow> queued,
        string movedId,
        int newIndex,
        IReadOnlyList<string> expected,
        IReadOnlyDictionary<string, int>? caps = null)
    {
        var changes = BoardModel.Reorder(queued, movedId, newIndex, caps ?? new Dictionary<string, int>());

        Assert.All(changes, c => Assert.NotEqual(c.From, c.To));
        Assert.All(changes, c => Assert.InRange(c.To, -1000, 1000));
        Assert.Equal(expected, BoardModel.Sorted(queued, changes));
        return changes;
    }

    private static WorkItemRow[] Fifo(params string[] ids)
        => [.. ids.Select((id, i) => Item(id, createdMinutesAgo: 100 - i))];

    [Fact]
    public void Moving_to_the_top_costs_one_priority_rewrite()
    {
        var queued = Fifo("a", "b", "c", "d", "e");
        var change = Assert.Single(Move(queued, "e", 0, ["e", "a", "b", "c", "d"]));

        Assert.Equal("e", change.Id);
        Assert.Equal(1, change.To);
    }

    [Fact]
    public void Moving_to_the_bottom_costs_one_priority_rewrite()
    {
        var queued = Fifo("a", "b", "c", "d", "e");
        var change = Assert.Single(Move(queued, "a", 4, ["b", "c", "d", "e", "a"]));

        Assert.Equal("a", change.Id);
        Assert.Equal(-1, change.To);
    }

    [Fact]
    public void Sitting_on_a_neighbours_priority_is_free_when_created_at_already_orders_it()
    {
        // The tie-break is a real ordering, not a formality: dropping d between two items that share a
        // priority costs one change and no numeric room at all.
        WorkItemRow[] queued =
        [
            Item("a", priority: 10, createdMinutesAgo: 100),
            Item("b", priority: 5, createdMinutesAgo: 90),
            Item("c", priority: 5, createdMinutesAgo: 70),
            Item("d", priority: 1, createdMinutesAgo: 80),
        ];

        var change = Assert.Single(Move(queued, "d", 2, ["a", "b", "d", "c"]));
        Assert.Equal(5, change.To);
    }

    [Fact]
    public void Moving_between_equal_priorities_against_FIFO_renumbers_the_smallest_run()
    {
        // a, b, c are all priority 0 and ordered by createdAt. Putting c second is impossible with any
        // single value — above b it would also be above a — so the run below it is renumbered instead.
        var queued = Fifo("a", "b", "c");
        var changes = Move(queued, "c", 1, ["a", "c", "b"]);

        Assert.Equal(2, changes.Count);
        Assert.DoesNotContain(changes, c => c.Id == "a");
    }

    [Fact]
    public void A_project_cap_that_forbids_the_obvious_value_moves_the_neighbours_instead()
    {
        WorkItemRow[] queued =
        [
            Item("high", priority: 5, project: "free", createdMinutesAgo: 100),
            Item("capped", priority: 0, project: "tight", createdMinutesAgo: 90),
        ];
        var caps = new Dictionary<string, int> { ["tight"] = 5 };

        var changes = Move(queued, "capped", 0, ["capped", "high"], caps);

        Assert.All(changes, c => Assert.True(c.Id != "capped" || c.To <= 5));
        Assert.Contains(changes, c => c.Id == "high");
    }

    [Fact]
    public void Everything_at_the_ceiling_is_still_reorderable_by_moving_the_rest_down()
    {
        WorkItemRow[] queued =
        [
            Item("a", priority: 1000, createdMinutesAgo: 100),
            Item("b", priority: 1000, createdMinutesAgo: 90),
            Item("c", priority: 1000, createdMinutesAgo: 80),
            Item("d", priority: 1000, createdMinutesAgo: 70),
        ];

        var changes = Move(queued, "d", 0, ["d", "a", "b", "c"]);

        Assert.DoesNotContain(changes, c => c.Id == "d");
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.True(c.To < 1000));
    }

    [Fact]
    public void Moving_something_to_where_it_already_is_rewrites_nothing()
    {
        var queued = Fifo("a", "b", "c");
        Assert.Empty(BoardModel.Reorder(queued, "b", 1, new Dictionary<string, int>()));
        Assert.Empty(BoardModel.Reorder(queued, "not-here", 0, new Dictionary<string, int>()));
    }

    // -----------------------------------------------------------------------------------------------
    // Cycles
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_dependency_that_would_close_a_loop_is_refused()
    {
        WorkItemRow[] items = [Item("a"), Item("b", dependsOn: ["a"]), Item("c", dependsOn: ["b"])];

        Assert.True(BoardModel.WouldCycle("a", ["c"], items));   // a <- c, and c already reaches a
        Assert.True(BoardModel.WouldCycle("a", ["a"], items));   // an item cannot wait on itself
        Assert.False(BoardModel.WouldCycle("c", ["a"], items));  // already true, and still acyclic
        Assert.False(BoardModel.WouldCycle("a", [], items));
    }

    // -----------------------------------------------------------------------------------------------
    // Cost
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Five_hundred_items_rebuild_in_well_under_a_second()
    {
        // The board rebuilds on every refresh, so this is a correctness property, not a nicety.
        var items = new List<WorkItemRow>();
        for (var i = 0; i < 420; i++)
        {
            items.Add(Item($"solo-{i:000}", i % 3 == 0 ? "Done" : "Queued", createdMinutesAgo: 1000 - i));
        }

        for (var chain = 0; chain < 10; chain++)
        {
            for (var step = 0; step < 8; step++)
            {
                items.Add(Item(
                    $"c{chain}-{step}",
                    step == 0 ? "Working" : "Queued",
                    title: $"Batch {chain} {step + 1}/8: step",
                    dependsOn: step == 0 ? null : [$"c{chain}-{step - 1}"],
                    satisfied: step == 0,
                    createdMinutesAgo: 500 - step));
            }
        }

        Assert.Equal(500, items.Count);

        var watch = Stopwatch.StartNew();
        var board = BoardModel.Build(items, [], 2, 2, Now);
        watch.Stop();

        Assert.Equal(10, board.Now.Count);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }
}
