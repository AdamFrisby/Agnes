using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// A permission request that nobody answered and nobody ever can. Both shapes here were taken from a
/// real host's event log: a Copilot session where the agent announced sixteen requests and ran the tools
/// anyway (it already had its own blanket permission), and an OpenCode session where requests were
/// withdrawn at the end of a turn. In both, the client kept offering answer buttons that would have gone
/// to nobody, and the cross-session inbox counted them as work waiting on a human — forever.
/// </summary>
public class PermissionExpiryTests
{
    private static PermissionRequestedEvent Ask(string requestId, string toolCallId)
        => new(requestId, toolCallId, "Access paths outside trusted directories", []);

    [Fact]
    public void The_agent_running_the_tool_anyway_expires_the_request()
    {
        var t = new TranscriptBuilder();
        var expired = new List<PermissionItem>();
        t.PermissionExpired += expired.Add;

        t.Apply(new ToolCallEvent("tc1", "bash", ToolKind.Execute, ToolCallStatus.InProgress, []));
        t.Apply(Ask("r1", "tc1"));
        Assert.NotNull(t.PendingPermission);

        // The very next thing in the real log: the gated call finishing. Nobody said yes.
        t.Apply(new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed, null));

        var card = Assert.Single(expired);
        Assert.True(card.Expired);
        Assert.False(card.IsAnswerable);
        Assert.Null(t.PendingPermission);   // and it stops claiming the answer bar
        Assert.True(card.CanSetStandingRule);
    }

    [Fact]
    public void A_turn_ending_expires_whatever_is_still_open()
    {
        var t = new TranscriptBuilder();
        t.Apply(Ask("r1", "tc1"));
        t.Apply(Ask("r2", "tc2"));
        t.Apply(new TurnEndedEvent(StopReason.EndTurn));

        var cards = t.Items.OfType<PermissionItem>().ToList();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.True(c.Expired));
    }

    [Fact]
    public void An_answered_request_is_never_retrospectively_expired()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "bash", ToolKind.Execute, ToolCallStatus.InProgress, []));
        t.Apply(Ask("r1", "tc1"));
        t.Apply(new PermissionResolvedEvent("r1", "allow", PermissionOutcome.Allowed));

        // The ordinary sequence ends with the approved call completing, and that must not read as
        // "the agent went ahead without us" — which it would if expiry were judged after the fact.
        t.Apply(new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed, null));
        t.Apply(new TurnEndedEvent(StopReason.EndTurn));

        var card = t.Items.OfType<PermissionItem>().Single();
        Assert.False(card.Expired);
        Assert.True(card.Resolved);
        Assert.Equal("Allowed", card.ResolutionText);
    }

    [Fact]
    public void A_live_request_stays_answerable()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "bash", ToolKind.Execute, ToolCallStatus.InProgress, []));
        t.Apply(Ask("r1", "tc1"));
        // Other work carries on around it; the turn hasn't ended and the gated call hasn't run.
        t.Apply(new ToolCallEvent("tc2", "read", ToolKind.Read, ToolCallStatus.Completed, []));
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("still working")));

        var card = t.Items.OfType<PermissionItem>().Single();
        Assert.False(card.Expired);
        Assert.True(card.IsAnswerable);
        Assert.NotNull(t.PendingPermission);
    }

    [Fact]
    public void A_withdrawn_request_says_so_instead_of_naming_the_wire_enum()
    {
        // "Cancelled" told the user nothing about why the buttons had gone, and put a protocol value
        // on screen besides. Nineteen of one real session's thirty requests ended this way.
        Assert.Equal("Withdrawn before it was answered", PermissionItem.OutcomeText(PermissionOutcome.Cancelled));
        Assert.Equal("Allowed", PermissionItem.OutcomeText(PermissionOutcome.Allowed));
        Assert.Equal("Denied", PermissionItem.OutcomeText(PermissionOutcome.Denied));
    }

    [Fact]
    public void The_batch_rule_and_the_live_rule_agree()
    {
        // The host scans a whole log; a client folds events in one at a time. They must reach the same
        // verdict, or the inbox and the session view disagree about what needs a human.
        SessionEvent[] log =
        [
            new ToolCallEvent("tc1", "bash", ToolKind.Execute, ToolCallStatus.InProgress, []),
            Ask("r1", "tc1"),
            new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed, null),   // r1 expires here
            new ToolCallEvent("tc2", "bash", ToolKind.Execute, ToolCallStatus.InProgress, []),
            Ask("r2", "tc2"),
            new PermissionResolvedEvent("r2", "allow", PermissionOutcome.Allowed),
            new ToolCallUpdateEvent("tc2", ToolCallStatus.Completed, null),
            new ToolCallEvent("tc3", "bash", ToolKind.Execute, ToolCallStatus.InProgress, []),
            Ask("r3", "tc3"),                                                 // still live
        ];

        Assert.Equal(["r1"], PermissionLifecycle.ExpiredRequests(log).OrderBy(x => x));

        var t = new TranscriptBuilder();
        foreach (var e in log)
        {
            t.Apply(e);
        }

        Assert.Equal(
            ["r1"],
            t.Items.OfType<PermissionItem>().Where(p => p.Expired).Select(p => p.RequestId));
    }
}
