using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.Tests;

public class TranscriptBuilderTests
{
    private static long _seq;

    private static T Seq<T>(T e) where T : SessionEvent => (T)(e with { Sequence = ++_seq });

    [Fact]
    public void Coalesces_consecutive_assistant_chunks_into_one_bubble()
    {
        var t = new TranscriptBuilder();
        t.Apply(Seq(new MessageChunkEvent(MessageRole.User, new TextContent("hi"))));
        t.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("Hello, "))));
        t.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("world"))));

        Assert.Equal(2, t.Items.Count);
        var user = Assert.IsType<MessageBubbleItem>(t.Items[0]);
        Assert.True(user.IsUser);
        var assistant = Assert.IsType<MessageBubbleItem>(t.Items[1]);
        Assert.Equal("Hello, world", assistant.Text);
    }

    [Fact]
    public void Tool_call_updates_in_place_and_splits_bubbles()
    {
        var t = new TranscriptBuilder();
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("before")));
        t.Apply(new ToolCallEvent("tc1", "Read a.cs", ToolKind.Read, ToolCallStatus.InProgress, []));
        t.Apply(new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed, null));
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("after")));

        Assert.Equal(3, t.Items.Count);
        var tool = Assert.IsType<ToolCallItem>(t.Items[1]);
        Assert.Equal(ToolCallStatus.Completed, tool.Status);
        // The chunk after the tool call starts a new bubble rather than merging with "before".
        Assert.Equal("before", ((MessageBubbleItem)t.Items[0]).Text);
        Assert.Equal("after", ((MessageBubbleItem)t.Items[2]).Text);
    }

    [Fact]
    public void Permission_request_is_tracked_then_resolved()
    {
        var t = new TranscriptBuilder();
        var options = new[] { new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce) };
        t.Apply(new PermissionRequestedEvent("req1", "tc1", "Run rm", options));

        Assert.NotNull(t.PendingPermission);
        Assert.Equal("req1", t.PendingPermission!.RequestId);

        t.Apply(new PermissionResolvedEvent("req1", "allow", PermissionOutcome.Allowed));

        Assert.Null(t.PendingPermission);
        var item = Assert.IsType<PermissionItem>(t.Items[0]);
        Assert.True(item.Resolved);
    }

    [Fact]
    public void Subagent_events_are_announced_and_tag_their_items()
    {
        var t = new TranscriptBuilder();
        SubagentStartedEvent? announced = null;
        t.SubagentAdded += s => announced = s;

        t.Apply(new SubagentStartedEvent("sub-1", "reviewer"));
        Assert.NotNull(announced);
        Assert.Equal("reviewer", announced!.Name);

        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("hi")) { AgentId = "sub-1" });
        var bubble = t.Items.OfType<MessageBubbleItem>().Single();
        Assert.Equal("sub-1", bubble.AgentId);
    }

    [Fact]
    public void Tool_call_records_elapsed_time_from_timestamps()
    {
        var t = new TranscriptBuilder();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        t.Apply(new ToolCallEvent("tc1", "a.cs", ToolKind.Edit, ToolCallStatus.InProgress, []) { Timestamp = start });

        var tool = t.Items.OfType<ToolCallItem>().Single();
        Assert.False(tool.HasDuration);

        t.Apply(new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed, null) { Timestamp = start.AddMilliseconds(1400) });
        Assert.True(tool.HasDuration);
        Assert.Equal("1.4s", tool.DurationText);
    }

    [Fact]
    public void Permission_card_derives_facts_from_the_linked_tool()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "build/", ToolKind.Delete, ToolCallStatus.Pending, []));
        t.Apply(new PermissionRequestedEvent("r1", "tc1", "Delete files in the working directory?",
        [
            new PermissionOption("once", "Allow once", PermissionOptionKind.AllowOnce),
            new PermissionOption("always", "Allow always", PermissionOptionKind.AllowAlways),
        ]));

        var perm = t.Items.OfType<PermissionItem>().Single();
        Assert.Equal(ToolKind.Delete, perm.ToolKind);
        Assert.Contains("build/", perm.ResourceText);
        Assert.False(perm.Reversible);
        Assert.Contains("Not easily reversible", perm.ReversibleText);
        Assert.True(perm.HasNarrowestHint); // both once and always offered
    }

    [Fact]
    public void Plan_updates_the_same_item()
    {
        var t = new TranscriptBuilder();
        t.Apply(new PlanEvent([new PlanEntry("a", "pending")]));
        t.Apply(new PlanEvent([new PlanEntry("a", "completed"), new PlanEntry("b", "pending")]));

        var plan = Assert.Single(t.Items.OfType<PlanItemView>());
        Assert.Equal(2, plan.Entries.Count);
    }

    [Fact]
    public void Claude_Agent_tool_registers_a_subagent_and_still_shows_as_a_tool_row()
    {
        var t = new TranscriptBuilder();
        SubagentStartedEvent? announced = null;
        t.SubagentAdded += s => announced = s;

        t.Apply(new ToolCallEvent("tc1", "Agent", ToolKind.Other, ToolCallStatus.InProgress,
            [new TextContent("{\"description\":\"Deep dive Agnes\",\"subagent_type\":\"Explore\"}")]));

        Assert.NotNull(announced);
        Assert.Equal("Deep dive Agnes", announced!.Name);        // name comes from the tool input
        Assert.Single(t.Items.OfType<ToolCallItem>());            // result still readable as a tool row
    }

    [Fact]
    public void Claude_TaskCreate_and_TaskUpdate_build_the_plan_panel_without_noisy_tool_rows()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("t1", "TaskCreate", ToolKind.Other, ToolCallStatus.Completed,
            [new TextContent("{\"subject\":\"Write docs\",\"description\":\"the readme\"}")]));
        t.Apply(new ToolCallEvent("t2", "TaskCreate", ToolKind.Other, ToolCallStatus.Completed,
            [new TextContent("{\"subject\":\"Add tests\"}")]));
        t.Apply(new ToolCallEvent("t3", "TaskUpdate", ToolKind.Other, ToolCallStatus.Completed,
            [new TextContent("{\"taskId\":\"1\",\"status\":\"completed\"}")]));

        Assert.Empty(t.Items.OfType<ToolCallItem>());             // task tools don't clutter the transcript
        var plan = Assert.Single(t.Items.OfType<PlanItemView>());
        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal("Write docs", plan.Entries[0].Content);
        Assert.Equal("completed", plan.Entries[0].Status);        // TaskUpdate(id=1) landed on the first task
        Assert.Equal("pending", plan.Entries[1].Status);

        // The same plan is the builder's own, so the sidebar shows it too — this is the whole point:
        // Claude never sends PlanEvent, so anything reading only that saw no plan at all.
        Assert.Same(plan, t.Plan);
    }

    [Fact]
    public void The_plan_is_announced_once_and_thereafter_ticks_along_in_place()
    {
        var t = new TranscriptBuilder();
        var announced = 0;
        t.PlanChanged += () => announced++;

        Assert.Null(t.Plan);
        t.Apply(new ToolCallEvent("t1", "TodoWrite", ToolKind.Other, ToolCallStatus.Completed,
            [new TextContent("""{"todos":[{"content":"Write docs","status":"in_progress"}]}""")]));

        var plan = t.Plan;
        Assert.NotNull(plan);
        Assert.Equal(1, announced);
        Assert.Equal("in_progress", plan!.Entries[0].Status);

        // Ticking one off and adding another updates the SAME view, so anything bound to it follows.
        t.Apply(new ToolCallEvent("t2", "TodoWrite", ToolKind.Other, ToolCallStatus.Completed,
            [new TextContent("""{"todos":[{"content":"Write docs","status":"completed"},{"content":"Add tests","status":"pending"}]}""")]));

        Assert.Same(plan, t.Plan);
        Assert.Equal(1, announced);               // no second announcement — it was updated, not replaced
        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal("completed", plan.Entries[0].Status);
        Assert.Single(t.Items.OfType<PlanItemView>());
    }

    [Fact]
    public void A_finished_plan_folds_its_completed_run_but_keeps_the_last_one()
    {
        var t = new TranscriptBuilder();
        t.Apply(new PlanEvent(
        [
            new PlanEntry("Read the code", "completed"),
            new PlanEntry("Write the fix", "completed"),
            new PlanEntry("Add a test", "completed"),
            new PlanEntry("Run the suite", "in_progress"),
            new PlanEntry("Push", "pending"),
        ]));

        var plan = t.Plan!;
        // A plan only ever grows, so by the end of a session the panel is a wall of ticks unless the
        // superseded ones fold away. What's left is "last thing done, then everything still to do".
        Assert.Equal(2, plan.HiddenCount);
        Assert.True(plan.HasHidden);
        Assert.Equal(["Add a test", "Run the suite", "Push"], plan.VisibleEntries.Select(e => e.Content));
        Assert.Equal("Show 2 completed", plan.MoreLabel);

        plan.ShowAll = true;
        Assert.Equal(5, plan.VisibleEntries.Count);
        Assert.Equal("Show less", plan.MoreLabel);
    }

    [Fact]
    public void A_plan_folds_nothing_until_folding_pays()
    {
        // One completed entry: a "show 1 more" control costs the reader more than the line it hides.
        var one = new PlanItemView { Entries = [new PlanEntry("a", "completed"), new PlanEntry("b", "pending")] };
        Assert.False(one.HasHidden);
        Assert.Equal(2, one.VisibleEntries.Count);

        // Nothing done yet: everything is outstanding, so everything shows.
        var fresh = new PlanItemView { Entries = [new PlanEntry("a", "in_progress"), new PlanEntry("b", "pending")] };
        Assert.False(fresh.HasHidden);

        // Completed work that comes *after* something unfinished is out-of-order progress, not history.
        var jumbled = new PlanItemView
        {
            Entries = [new PlanEntry("a", "pending"), new PlanEntry("b", "completed"), new PlanEntry("c", "completed")],
        };
        Assert.False(jumbled.HasHidden);
    }

    [Fact]
    public void Folding_follows_the_plan_as_it_is_ticked_off()
    {
        var plan = new PlanItemView { Entries = [new PlanEntry("a", "in_progress"), new PlanEntry("b", "pending"), new PlanEntry("c", "pending")] };
        var raised = new List<string>();
        plan.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        plan.Entries = [new PlanEntry("a", "completed"), new PlanEntry("b", "completed"), new PlanEntry("c", "in_progress")];

        Assert.Equal(1, plan.HiddenCount);
        Assert.Equal(["b", "c"], plan.VisibleEntries.Select(e => e.Content));
        Assert.Contains(nameof(PlanItemView.VisibleEntries), raised);
        Assert.Contains(nameof(PlanItemView.HasHidden), raised);
    }

    [Fact]
    public void A_plan_event_and_the_task_tools_land_on_one_plan()
    {
        var t = new TranscriptBuilder();
        t.Apply(new PlanEvent([new PlanEntry("from acp", "pending")]));
        t.Apply(new ToolCallEvent("t1", "TodoWrite", ToolKind.Other, ToolCallStatus.Completed,
            [new TextContent("""{"todos":[{"content":"from claude","status":"pending"}]}""")]));

        Assert.Single(t.Items.OfType<PlanItemView>());
        Assert.Equal("from claude", t.Plan!.Entries[0].Content);
    }

    [Fact]
    public void QuestionAsked_becomes_a_pending_question_that_resolves_when_answered()
    {
        var t = new TranscriptBuilder();
        var changes = 0;
        t.PendingQuestionChanged += () => changes++;

        t.Apply(new QuestionAskedEvent("r1", "tu1",
        [
            new AgentQuestion("db", "DB", "Which database?", [new QuestionChoice("SQLite", "local"), new QuestionChoice("Postgres", "server")]),
            new AgentQuestion("f", "Features", "Which features?", [new QuestionChoice("Auth", "")], MultiSelect: true),
        ]));

        var item = Assert.Single(t.Items.OfType<QuestionItem>());
        Assert.Same(item, t.PendingQuestion);
        Assert.False(item.Resolved);
        Assert.Equal(2, item.Questions.Count);
        Assert.True(item.Questions[1].MultiSelect);

        // Selecting + building answers reflects the choice and notes.
        item.Questions[0].Options[0].IsSelected = true;
        item.Questions[0].Notes = "zero-config please";
        var answers = item.BuildAnswers();
        Assert.Equal(["SQLite"], answers[0].SelectedLabels);
        Assert.Equal("zero-config please", answers[0].Notes);

        t.Apply(new QuestionAnsweredEvent("r1"));
        Assert.True(item.Resolved);
        Assert.Null(t.PendingQuestion);
        Assert.Equal(2, changes); // raised on ask + on answer
    }

    private static string EditInput(string path, string oldText, string newText)
        => $$"""{"file_path":"{{path}}","old_string":{{Json(oldText)}},"new_string":{{Json(newText)}}}""";

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void An_edit_keeps_its_diff_even_though_the_result_is_only_a_confirmation()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "src/a.cs", ToolKind.Edit, ToolCallStatus.InProgress,
            [new TextContent(EditInput("src/a.cs", "var x = 1;", "var x = 2;"))]));
        // What Claude actually sends back when an edit lands — a receipt, with no trace of the change.
        t.Apply(new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed,
            [new TextContent("The file src/a.cs has been updated successfully.")]));

        var tool = Assert.Single(t.Items.OfType<ToolCallItem>());
        Assert.True(tool.HasDiff);
        Assert.Contains("-var x = 1;", tool.PreviewBody, StringComparison.Ordinal);
        Assert.Contains("+var x = 2;", tool.PreviewBody, StringComparison.Ordinal);
        Assert.DoesNotContain("updated successfully", tool.PreviewBody, StringComparison.Ordinal);
        Assert.Equal("+1  −1", tool.DiffSummary);
    }

    [Fact]
    public void A_structured_diff_from_an_acp_agent_is_used_as_is()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "src/a.cs", ToolKind.Edit, ToolCallStatus.Completed,
            [new DiffContent("src/a.cs", "one\n", "two\n")]));

        var tool = Assert.Single(t.Items.OfType<ToolCallItem>());
        Assert.True(tool.HasDiff);
        Assert.Contains("+two", tool.PreviewBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_edit_tool_has_no_diff_and_previews_its_output()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "ls -la", ToolKind.Execute, ToolCallStatus.InProgress, []));
        t.Apply(new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed, [new TextContent("total 8\na\nb")]));

        var tool = Assert.Single(t.Items.OfType<ToolCallItem>());
        Assert.False(tool.HasDiff);
        Assert.Equal("total 8\na\nb", tool.PreviewBody);
    }

    [Fact]
    public void A_small_edit_shows_inline_and_a_large_one_does_not()
    {
        var small = Tool(EditInput("a.cs", "one", "two"));
        Assert.True(small.HasInlineDiff);
        Assert.False(small.HasCollapsedDiff);
        // Headers are dropped inline: an Edit's hunk header always claims line 1 whatever it touched.
        Assert.DoesNotContain(small.InlineDiffLines, l => l.IsHunk);

        var manyChanges = Tool(EditInput("a.cs", "x", string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i}"))));
        Assert.False(manyChanges.HasInlineDiff);
        Assert.True(manyChanges.HasCollapsedDiff);

        // A two-line change buried in a wall of unchanged context is small by change count but not on screen.
        var context = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"line {i}"));
        var wideContext = Tool(EditInput("a.cs", context + "\nold", context + "\nnew"));
        Assert.False(wideContext.HasInlineDiff);
    }

    private static ToolCallItem Tool(string input)
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc", "a.cs", ToolKind.Edit, ToolCallStatus.Completed, [new TextContent(input)]));
        return t.Items.OfType<ToolCallItem>().Single();
    }

    [Fact]
    public void A_permission_carries_the_full_command_it_is_asking_about()
    {
        var command = "rm -rf " + new string('x', 400);
        var t = new TranscriptBuilder();
        t.Apply(new PermissionRequestedEvent("r1", "tc1", "Allow Bash?",
            [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce)], command));

        var item = Assert.Single(t.Items.OfType<PermissionItem>());
        Assert.True(item.HasDetail);
        Assert.Equal(command, item.Detail); // whole, not a prefix — the tail is the dangerous part
    }

    [Fact]
    public void A_permission_without_its_own_detail_falls_back_to_the_linked_tool()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "git push --force", ToolKind.Execute, ToolCallStatus.Pending, []));
        t.Apply(new PermissionRequestedEvent("r1", "tc1", "Allow Bash?",
            [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce)]));

        var item = Assert.Single(t.Items.OfType<PermissionItem>());
        Assert.Equal("git push --force", item.Detail);
    }

    [Fact]
    public void A_tool_summary_is_not_truncated_by_the_view_model()
    {
        var line = new string('a', 500);
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "grep", ToolKind.Search, ToolCallStatus.Completed, [new TextContent(line + "\nsecond")]));

        var tool = Assert.Single(t.Items.OfType<ToolCallItem>());
        Assert.Equal(line, tool.Summary); // one line, but all of it — the view ellipsizes to fit its row
    }
}
