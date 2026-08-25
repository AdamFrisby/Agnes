using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// "The last turn was interrupted." is a claim about the present, and it used to be a claim nothing could
/// ever falsify: an AgentErrorEvent raised it and only a human clicking Dismiss lowered it again. A session
/// that hit a rate limit and then carried on kept the banner up over the top of the agent visibly working,
/// and reconnecting re-raised an error from hours earlier because the recovery that followed it said
/// nothing to the contrary.
/// </summary>
public class InterruptedBannerTests
{
    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "copilot", string.Empty, 0), [], 0));
        return view;
    }

    private static SessionView History(params SessionEvent[] events)
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "copilot", string.Empty, 0), events, events.Length));
        return view;
    }

    private static SessionViewModel Open(SessionView view, FakeHost host)
        => new(host, view, ImmediateDispatcher.Instance, "Copilot");

    [Theory]
    [InlineData("message")]
    [InlineData("thought")]
    [InlineData("tool")]
    public void The_agent_working_again_retracts_the_banner(string evidence)
    {
        var view = Live();
        var vm = Open(view, new FakeHost());

        view.Apply(Seq(new AgentErrorEvent("429 rate limited"), 1));
        Assert.Equal(SessionBanner.Interrupted, vm.Banner);
        Assert.Equal(SessionActivity.Error, vm.Activity);

        SessionEvent progress = evidence switch
        {
            "message" => new MessageChunkEvent(MessageRole.Assistant, new TextContent("carrying on")),
            "thought" => new ThoughtChunkEvent(new TextContent("thinking again")),
            _ => new ToolCallEvent("tc1", "bash", ToolKind.Execute, ToolCallStatus.InProgress, []),
        };
        view.Apply(Seq(progress, 2));

        // Updates are arriving from the agent, so the interruption is over — whatever it was.
        Assert.Equal(SessionBanner.None, vm.Banner);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public void A_turn_that_ends_normally_also_settles_it()
    {
        var view = Live();
        var vm = Open(view, new FakeHost());

        view.Apply(Seq(new AgentErrorEvent("transient failure"), 1));
        view.Apply(Seq(new TurnEndedEvent(StopReason.EndTurn), 2));

        Assert.Equal(SessionBanner.None, vm.Banner);
    }

    [Fact]
    public void The_refusal_that_accompanies_the_error_does_not_clear_it()
    {
        var view = Live();
        var vm = Open(view, new FakeHost());

        // The native Claude mapper reports one failure as both events, in this order. Clearing on any
        // turn end would erase the banner one event after raising it.
        view.Apply(Seq(new AgentErrorEvent("credential expired"), 1));
        view.Apply(Seq(new TurnEndedEvent(StopReason.Refusal), 2));

        Assert.Equal(SessionBanner.Interrupted, vm.Banner);
        Assert.True(vm.CanRetry);
    }

    [Fact]
    public void A_replayed_log_reflects_how_it_ended_not_the_worst_moment_in_it()
    {
        // Reconnecting to a session that failed and recovered used to reinstate the failure, because
        // replay raised the flag and nothing in the rest of the log lowered it.
        var vm = Open(
            History(
                Seq(new AgentErrorEvent("429 rate limited"), 1),
                Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("retrying")), 2),
                Seq(new ToolCallEvent("tc1", "bash", ToolKind.Execute, ToolCallStatus.Completed, []), 3),
                Seq(new TurnEndedEvent(StopReason.EndTurn), 4)),
            new FakeHost());

        Assert.Equal(SessionBanner.None, vm.Banner);
    }

    [Fact]
    public void A_log_that_ends_in_failure_still_says_so()
    {
        var vm = Open(
            History(
                Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("working")), 1),
                Seq(new AgentErrorEvent("429 rate limited"), 2)),
            new FakeHost());

        // The point is evidence, not optimism: nothing followed the error, so it stands.
        Assert.Equal(SessionBanner.Interrupted, vm.Banner);
        Assert.Equal(SessionActivity.Error, vm.Activity);
    }

    [Fact]
    public void A_connection_problem_outranks_a_retracted_interruption()
    {
        var view = Live();
        var host = new FakeHost();
        var vm = Open(view, host);

        view.Apply(Seq(new AgentErrorEvent("boom"), 1));
        host.SetState(AgnesConnectionState.Disconnected);
        Assert.Equal(SessionBanner.Offline, vm.Banner);

        // Clearing the interruption must not paper over the host still being unreachable.
        view.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("hi")), 2));
        Assert.Equal(SessionBanner.Offline, vm.Banner);

        host.SetState(AgnesConnectionState.Connected);
        Assert.Equal(SessionBanner.None, vm.Banner);
    }
}
