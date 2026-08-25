using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// Opening a session replays its whole log through the same code path a live event takes. Rebuilding the
/// transcript is right; re-acting on it is not. Without a guard, every reconnect re-answered every request
/// still open in the log and re-raised a notification for each — which is how one host accumulated 218
/// discarded permission responses against a session that had asked 17 times, and why "always allow"
/// appeared to do nothing: the auto-answer landed on a request the agent had long since abandoned.
/// </summary>
public class PermissionReplayTests
{
    private sealed class Always : IPermissionPolicy
    {
        public int Decisions { get; private set; }

        public bool? Decide(string hostUrl, ToolKind? toolKind)
        {
            Decisions++;
            return true;
        }

        public void Remember(string hostUrl, ToolKind? toolKind, bool allow) { }
        public void Forget(string hostUrl, ToolKind? toolKind) { }
    }

    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    private static SessionView History(params SessionEvent[] events)
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "copilot", string.Empty, 0), events, events.Length));
        return view;
    }

    private static SessionViewModel Open(SessionView view, IPermissionPolicy policy, out List<AppNotification> notifications)
    {
        var vm = new SessionViewModel(new SimulatedHost(), view, ImmediateDispatcher.Instance, "Copilot", policy: policy);
        var raised = new List<AppNotification>();
        vm.NotificationRaised += raised.Add;
        notifications = raised;
        return vm;
    }

    [Fact]
    public void Replaying_a_log_of_withdrawn_requests_answers_none_of_them()
    {
        var policy = new Always();

        // The real shape: Copilot asks, then runs the tool anyway, sixteen times over.
        var events = new List<SessionEvent>();
        for (var i = 0; i < 16; i++)
        {
            events.Add(Seq(new ToolCallEvent($"tc{i}", "read", ToolKind.Read, ToolCallStatus.InProgress, []), i * 3 + 1));
            events.Add(Seq(new PermissionRequestedEvent($"r{i}", $"tc{i}", "Access paths outside trusted directories", []), i * 3 + 2));
            events.Add(Seq(new ToolCallUpdateEvent($"tc{i}", ToolCallStatus.Completed, null), i * 3 + 3));
        }

        var vm = Open(History([.. events]), policy, out var notifications);

        // Not one is answered: they are all history, and all withdrawn besides.
        Assert.Equal(0, policy.Decisions);
        Assert.Null(vm.PendingPermission);
        // Nor does reconnecting ring the doorbell sixteen times for requests nobody can act on.
        Assert.Empty(notifications);
        // They are still there to look at, and to set a standing rule from.
        Assert.Equal(16, vm.ExpiredApprovals.Count());
        Assert.True(vm.HasExpiredApprovals);
    }

    [Fact]
    public void A_request_still_live_after_replay_is_answered_once()
    {
        var policy = new Always();
        var vm = Open(
            History(
                Seq(new ToolCallEvent("tc0", "read", ToolKind.Read, ToolCallStatus.InProgress, []), 1),
                Seq(new PermissionRequestedEvent("r0", "tc0", "Access paths outside trusted directories", []), 2),
                Seq(new ToolCallUpdateEvent("tc0", ToolCallStatus.Completed, null), 3),   // withdrawn
                Seq(new ToolCallEvent("tc1", "read", ToolKind.Read, ToolCallStatus.InProgress, []), 4),
                Seq(new PermissionRequestedEvent("r1", "tc1", "Access paths outside trusted directories", []), 5)),
            policy,
            out _);

        // Exactly one decision, on the one request that is genuinely still waiting — not one per row in
        // the log, and not on the request the agent already gave up on.
        Assert.Equal(1, policy.Decisions);
        Assert.Equal("r1", vm.PendingPermission?.RequestId);
    }

    [Fact]
    public void Turn_ends_in_history_do_not_announce_themselves()
    {
        var vm = Open(
            History(
                Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("done")), 1),
                Seq(new TurnEndedEvent(StopReason.EndTurn), 2),
                Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("done again")), 3),
                Seq(new TurnEndedEvent(StopReason.EndTurn), 4),
                Seq(new AgentErrorEvent("rate limited"), 5)),
            NullPermissionPolicy.Instance,
            out var notifications);

        // Reopening a tab used to pop a toast for every turn the session had ever finished.
        Assert.Empty(notifications);
        Assert.NotNull(vm);
    }

    [Fact]
    public void A_live_event_after_replay_still_notifies()
    {
        var view = History(Seq(new TurnEndedEvent(StopReason.EndTurn), 1));
        var vm = Open(view, NullPermissionPolicy.Instance, out var notifications);
        Assert.Empty(notifications);

        // The guard covers the constructor's catch-up only; everything after it is live.
        view.Apply(Seq(new TurnEndedEvent(StopReason.EndTurn), 2));

        var one = Assert.Single(notifications);
        Assert.Equal(NotificationKind.Completion, one.Kind);
        Assert.NotNull(vm);
    }
}
