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
}
