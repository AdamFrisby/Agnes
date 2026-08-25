using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// OpenCode delegates through a <c>task</c> tool whose result is an XML-ish envelope naming the subagent
/// it started. Untranslated, that envelope rendered verbatim in the transcript as a "Think" row — dozens
/// of identical <c>&lt;task id="ses_…" state="running"&gt;</c> lines carrying instructions written for the
/// model, and no subagent anywhere in the session's roster. These cover the translation.
/// </summary>
public class SubagentTaskTests
{
    private const string Launched = """
        <task id="ses_fc7e2b664ffeFOKOShIfVT0UzC" state="running">
        <summary>Background task started</summary>
        <task_result>
        The task is working in the background. You will be notified automatically when it finishes.
        DO NOT sleep, poll for progress, ask the task for status, or duplicate this task's work.
        </task_result>
        </task>
        """;

    private const string Finished = """
        <task id="ses_fc7e2b664ffeFOKOShIfVT0UzC" state="completed">
        <task_result>
        Ported 17TRACK. All 13 built individually, one commit per project, nothing pushed.
        </task_result>
        </task>
        """;

    private static ToolCallEvent Call(string id) => new(id, "task", ToolKind.Think, ToolCallStatus.InProgress, []);

    private static ToolCallUpdateEvent Result(string id, string payload)
        => new(id, ToolCallStatus.Completed, [new TextContent(payload)]);

    [Fact]
    public void Reads_the_subagent_id_and_state_out_of_the_envelope()
    {
        var launched = SubagentTaskPayload.TryParse(Launched)!;
        Assert.Equal("ses_fc7e2b664ffeFOKOShIfVT0UzC", launched.TaskId);
        Assert.True(launched.IsRunning);
        // The launch's <task_result> is addressed to the model, not to a person — it isn't a report.
        Assert.Equal(string.Empty, launched.Body);

        var finished = SubagentTaskPayload.TryParse(Finished)!;
        Assert.False(finished.IsRunning);
        Assert.StartsWith("Ported 17TRACK.", finished.Body);
    }

    [Fact]
    public void Ordinary_tool_output_is_not_mistaken_for_a_delegation()
    {
        Assert.Null(SubagentTaskPayload.TryParse(null));
        Assert.Null(SubagentTaskPayload.TryParse(""));
        Assert.Null(SubagentTaskPayload.TryParse("wrote 12 lines to src/config.ts"));
        // Shape, not vocabulary: prose mentioning a task is still prose.
        Assert.Null(SubagentTaskPayload.TryParse("<task> started the task </task>"));
    }

    [Fact]
    public void A_launched_subagent_joins_the_roster_and_stays_running()
    {
        var t = new TranscriptBuilder();
        var announced = new List<SubagentStartedEvent>();
        var finished = new List<string>();
        t.SubagentAdded += announced.Add;
        t.SubagentFinished += finished.Add;

        t.Apply(Call("call_1"));
        t.Apply(Result("call_1", Launched));

        var announcedOne = Assert.Single(announced);
        // The subagent's identity is OpenCode's task id, not the tool call's: the call that reports the
        // subagent finished is a *different* call, and only this id ties the two together.
        Assert.Equal("ses_fc7e2b664ffeFOKOShIfVT0UzC", announcedOne.SubagentId);
        Assert.Equal("Subagent 1", announcedOne.Name);
        Assert.Empty(finished);

        var row = Assert.IsType<ToolCallItem>(t.Items.Single());
        Assert.Equal(ToolKind.Subagent, row.Kind);
        Assert.Equal("Subagent 1", row.Title);
        Assert.Equal("ses_fc7e2b664ffeFOKOShIfVT0UzC", row.AgentId);
        // The transport called the *launch* complete after a few seconds; the subagent is still working.
        Assert.True(row.IsRunning);
        Assert.False(row.HasDuration);
        // And the envelope is gone — nothing of the raw markup survives into the transcript.
        Assert.DoesNotContain("<task", row.Detail);
        Assert.DoesNotContain("DO NOT sleep", row.Detail);
    }

    [Fact]
    public void The_later_call_that_reports_it_finished_retires_the_same_subagent()
    {
        var t = new TranscriptBuilder();
        var announced = new List<SubagentStartedEvent>();
        var finished = new List<string>();
        t.SubagentAdded += announced.Add;
        t.SubagentFinished += finished.Add;

        t.Apply(Call("call_1"));
        t.Apply(Result("call_1", Launched));
        t.Apply(Call("call_2"));                 // a different tool call…
        t.Apply(Result("call_2", Finished));     // …reporting the same subagent's result

        // One subagent, not two: the roster is keyed by the task id both calls name.
        Assert.Single(announced);
        Assert.Equal(["ses_fc7e2b664ffeFOKOShIfVT0UzC"], finished);

        var report = (ToolCallItem)t.Items[1];
        Assert.Equal("Subagent 1", report.Title);
        Assert.True(report.IsDone);
        // What it actually said is what the row shows.
        Assert.StartsWith("Ported 17TRACK.", report.Detail);
    }

    [Fact]
    public void Subagents_are_numbered_in_the_order_the_log_reports_them()
    {
        var t = new TranscriptBuilder();
        var announced = new List<SubagentStartedEvent>();
        t.SubagentAdded += announced.Add;

        t.Apply(Call("call_1"));
        t.Apply(Result("call_1", Launched));
        t.Apply(Call("call_2"));
        t.Apply(Result("call_2", Launched.Replace("ses_fc7e2b664ffeFOKOShIfVT0UzC", "ses_other", StringComparison.Ordinal)));

        // Numbering comes off the event log, which every client replays identically — so the same
        // subagent is "Subagent 2" on the desktop, the phone and the web head alike.
        Assert.Equal(["Subagent 1", "Subagent 2"], announced.Select(a => a.Name));
    }

    [Fact]
    public void Claudes_own_subagent_tool_is_untouched()
    {
        var t = new TranscriptBuilder();
        var announced = new List<SubagentStartedEvent>();
        t.SubagentAdded += announced.Add;

        t.Apply(new ToolCallEvent("tc-1", "Task", ToolKind.Other, ToolCallStatus.InProgress,
            [new TextContent("""{"description":"review the diff","subagent_type":"code-reviewer"}""")]));

        // Claude names its subagent in the call, so it registers immediately and keeps the tool-call id
        // as its identity — the child's own events are already tagged with it.
        var one = Assert.Single(announced);
        Assert.Equal("tc-1", one.SubagentId);
        Assert.Equal("review the diff", one.Name);
        Assert.Equal("Task", ((ToolCallItem)t.Items.Single()).Title);
    }
}
