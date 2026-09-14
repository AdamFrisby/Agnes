using Agnes.Abstractions;
using Agnes.Protocol;
using Agnes.Client;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The agents roster: which rows it shows, and when a background subagent stops counting as running.
/// One live OpenCode session had dispatched 684 subagents over its life, 651 of them never reported on,
/// and the roster showed all 651 as running — 7,000 controls in a side panel, and a claim that was not
/// true. These pin the two halves of the fix: the rows are bounded, and a turn end retires them.
/// </summary>
public class RosterTests
{
    private const string Session = "s1";

    private static string Launched(string id) => $"""
        <task id="{id}" state="running">
        <summary>Background task started</summary>
        <task_result>The task is working in the background.</task_result>
        </task>
        """;

    private static string Finished(string id) => $"""
        <task id="{id}" state="completed">
        <task_result>Done: ported it.</task_result>
        </task>
        """;

    private sealed class Host : StubAgnesHost;

    private static (SessionViewModel vm, SessionView view) Open()
    {
        var view = new SessionView(Session);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(Session, "opencode", string.Empty, 0), [], 0));
        return (new SessionViewModel(new Host(), view, ImmediateDispatcher.Instance, "OpenCode"), view);
    }

    private static long Launch(SessionView view, long seq, string call, string task)
    {
        view.Apply(new ToolCallEvent(call, "task", ToolKind.Think, ToolCallStatus.InProgress, []) { Sequence = seq++ });
        view.Apply(new ToolCallUpdateEvent(call, ToolCallStatus.Completed, [new TextContent(Launched(task))]) { Sequence = seq++ });
        return seq;
    }

    [Fact]
    public void A_background_subagent_nobody_reported_on_stops_running_when_the_turn_ends()
    {
        var (vm, view) = Open();
        var seq = Launch(view, 1, "call_1", "ses_a");
        seq = Launch(view, seq, "call_2", "ses_b");

        Assert.Equal(2, vm.VisibleAgentRows.Count(n => !n.IsMain && n.IsActive));

        view.Apply(new TurnEndedEvent(StopReason.EndTurn) { Sequence = seq++ });

        // The turn that dispatched them is over and nothing will report on them: not running.
        Assert.DoesNotContain(vm.VisibleAgentRows, n => !n.IsMain);
        Assert.True(vm.HasInactiveAgents);
        Assert.Equal("Show all 2", vm.MoreAgentsLabel);

        // A later report that it is still running puts it back; one that it finished keeps it retired.
        view.Apply(new ToolCallEvent("call_3", "task", ToolKind.Think, ToolCallStatus.InProgress, []) { Sequence = seq++ });
        view.Apply(new ToolCallUpdateEvent("call_3", ToolCallStatus.Completed, [new TextContent(Launched("ses_a"))]) { Sequence = seq++ });
        Assert.Equal(["ses_a"], vm.VisibleAgentRows.Where(n => !n.IsMain).Select(n => n.Id));

        view.Apply(new ToolCallEvent("call_4", "task", ToolKind.Think, ToolCallStatus.InProgress, []) { Sequence = seq++ });
        view.Apply(new ToolCallUpdateEvent("call_4", ToolCallStatus.Completed, [new TextContent(Finished("ses_a"))]) { Sequence = seq++ });
        Assert.DoesNotContain(vm.VisibleAgentRows, n => !n.IsMain);
    }

    [Fact]
    public void The_roster_is_bounded_however_many_subagents_a_session_dispatches()
    {
        var (vm, view) = Open();
        long seq = 1;
        for (var i = 0; i < 150; i++)
        {
            seq = Launch(view, seq, $"call_{i}", $"ses_{i:000}");
        }

        // Running, mid-turn: the newest twenty, and the main agent.
        var shown = vm.VisibleAgentRows.ToList();
        Assert.Equal(1 + SessionViewModel.AgentDisplayLimit, shown.Count);
        Assert.True(shown[0].IsMain);
        Assert.Equal("ses_149", shown[^1].Id);
        Assert.Equal("ses_130", shown[1].Id);
        Assert.True(vm.HasInactiveAgents);
        Assert.Equal("Show all 150", vm.MoreAgentsLabel);
        Assert.Equal(string.Empty, vm.AgentRowsNote);

        // "Show all" is a page, and says so.
        vm.ShowAllAgents = true;
        Assert.Equal(1 + SessionViewModel.AgentPageLimit, vm.VisibleAgentRows.Count());
        Assert.Equal("Latest 100 of 150", vm.AgentRowsNote);
        Assert.False(vm.HasInactiveAgents);
    }

    [Fact]
    public void The_glyph_is_the_nodes_own()
    {
        var (vm, view) = Open();
        Launch(view, 1, "call_1", "ses_a");
        var rows = vm.VisibleAgentRows.ToList();
        Assert.Equal(FluentIcons.Common.Symbol.Diamond, rows[0].Glyph);
        Assert.Equal(FluentIcons.Common.Symbol.Circle, rows[1].Glyph);
    }
}
