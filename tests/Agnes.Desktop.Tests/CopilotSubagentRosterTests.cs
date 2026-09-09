using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// A Copilot subagent, end to end: announced at the ACP boundary from the call's rawInput, into the agent
/// roster, and selectable. The gap this closes is that Copilot dispatches in the background — none of its
/// subagent's inner work streams back, and every event in a real session carries a null agent id — so a
/// view filtered strictly by agent id would have been empty and the roster row would have looked broken.
/// </summary>
public class CopilotSubagentRosterTests
{
    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    private static (SessionViewModel Vm, SessionView View) Open()
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "copilot", string.Empty, 0), [], 0));
        return (new SessionViewModel(new SimulatedHost(), view, ImmediateDispatcher.Instance, "Copilot"), view);
    }

    [Fact]
    public void A_dispatched_subagent_reaches_the_roster_and_shows_its_own_work()
    {
        var (vm, view) = Open();
        Assert.False(vm.HasSubagents);

        // What AcpMap now produces for `task`: the ordinary row, plus the roster entry.
        view.Apply(Seq(new ToolCallEvent("tooluse_1", "Explore extension spec format", ToolKind.Other, ToolCallStatus.InProgress, []), 1));
        view.Apply(Seq(new SubagentStartedEvent("tooluse_1", "Explore extension spec format"), 2));

        Assert.True(vm.HasSubagents);
        var row = Assert.Single(vm.VisibleAgentRows, r => !r.IsMain);
        Assert.Equal("Explore extension spec format", row.Name);

        // Selecting it shows the launch — which for a background dispatch is the only thing there is.
        row.SelectCommand.Execute(null);
        Assert.Equal("tooluse_1", vm.SelectedAgentId);
        var shown = Assert.IsType<ToolCallItem>(Assert.Single(vm.DisplayItems));
        Assert.Equal("Explore extension spec format", shown.Title);
        Assert.False(vm.IsTranscriptEmpty);
    }

    [Fact]
    public void Its_result_lands_in_the_subagent_view_too()
    {
        var (vm, view) = Open();
        view.Apply(Seq(new ToolCallEvent("tooluse_1", "Explore extension spec format", ToolKind.Other, ToolCallStatus.InProgress, []), 1));
        view.Apply(Seq(new SubagentStartedEvent("tooluse_1", "Explore extension spec format"), 2));
        view.Apply(Seq(new ToolCallUpdateEvent("tooluse_1", ToolCallStatus.Completed, [new TextContent("Found 28 extension specs.")]), 3));

        vm.AgentTree[0].Children[0].SelectCommand.Execute(null);
        var shown = Assert.IsType<ToolCallItem>(Assert.Single(vm.DisplayItems));
        Assert.Equal("Found 28 extension specs.", shown.Detail);
        Assert.True(shown.IsDone);
    }

    [Fact]
    public void The_main_conversation_keeps_the_launch_row()
    {
        var (vm, view) = Open();
        view.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("Dispatching an explorer.")), 1));
        view.Apply(Seq(new ToolCallEvent("tooluse_1", "Explore extension spec format", ToolKind.Other, ToolCallStatus.InProgress, []), 2));
        view.Apply(Seq(new SubagentStartedEvent("tooluse_1", "Explore extension spec format"), 3));

        // Announcing a subagent must not take the call out of the parent's transcript: that row is the
        // parent's own action, and it is where the result is read.
        Assert.Equal(2, vm.DisplayItems.Count());
    }

    [Fact]
    public void One_subagent_reported_twice_produces_one_row()
    {
        var (vm, view) = Open();

        // OpenCode reports the same dispatch through two channels: rawInput at the boundary (named), and
        // its own <task id=…> envelope in the result (an opaque ses_ id). Both must land on one row.
        view.Apply(Seq(new ToolCallEvent("call_1", "task", ToolKind.Subagent, ToolCallStatus.InProgress, []), 1));
        view.Apply(Seq(new SubagentStartedEvent("call_1", "Port the 17TRACK plugin"), 2));
        view.Apply(Seq(new ToolCallUpdateEvent("call_1", ToolCallStatus.Completed,
            [new TextContent("""
                <task id="ses_fc7e2b664ffe" state="running">
                <task_result>The task is working in the background.</task_result>
                </task>
                """)]), 3));

        var row = Assert.Single(vm.AgentTree[0].Children);
        // The name from rawInput wins over the id — "Port the 17TRACK plugin" beats "Subagent 1".
        Assert.Equal("Port the 17TRACK plugin", row.Name);
        // And the payload's state still governs: the launch call completed, the subagent has not.
        Assert.True(row.IsActive);
        Assert.True(((ToolCallItem)vm.Items[0]).IsRunning);
    }

    [Fact]
    public void The_second_report_can_still_retire_it()
    {
        var (vm, view) = Open();
        view.Apply(Seq(new ToolCallEvent("call_1", "task", ToolKind.Subagent, ToolCallStatus.InProgress, []), 1));
        view.Apply(Seq(new SubagentStartedEvent("call_1", "Port the 17TRACK plugin"), 2));
        view.Apply(Seq(new ToolCallUpdateEvent("call_1", ToolCallStatus.Completed,
            [new TextContent("""
                <task id="ses_fc7e2b664ffe" state="completed">
                <task_result>Ported 17TRACK; all 13 built.</task_result>
                </task>
                """)]), 3));

        var row = Assert.Single(vm.AgentTree[0].Children);
        Assert.False(row.IsActive);
        Assert.Equal("Ported 17TRACK; all 13 built.", ((ToolCallItem)vm.Items[0]).Detail);
    }
}
