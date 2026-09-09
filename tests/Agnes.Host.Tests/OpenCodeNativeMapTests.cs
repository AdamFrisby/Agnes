using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.OpenCode.Native;

namespace Agnes.Host.Tests;

/// <summary>
/// Mapping OpenCode's native event stream. The point of this adapter is the events ACP cannot express — a
/// failed step and a provider retry — so those are pinned hardest: over ACP the first arrives as an ordinary
/// turn end and the second doesn't arrive at all.
/// </summary>
public sealed class OpenCodeNativeMapTests
{
    private static IReadOnlyList<SessionEvent> Map(string json)
        => OpenCodeEventMap.ToEvents(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void A_finished_text_block_becomes_an_assistant_message()
    {
        var events = Map("""
            {"type":"session.next.text.ended",
             "data":{"timestamp":1,"sessionID":"ses_1","assistantMessageID":"msg_1","textID":"t1",
                     "text":"All 33 tests pass."}}
            """);

        var message = Assert.IsType<MessageChunkEvent>(Assert.Single(events));
        Assert.Equal(MessageRole.Assistant, message.Role);
        Assert.Equal("All 33 tests pass.", Assert.IsType<TextContent>(message.Content).Text);
    }

    [Fact]
    public void Reasoning_becomes_a_thought_not_a_message()
    {
        // Reasoning must never read as an answer — the stall detector counts one and not the other.
        var events = Map("""
            {"type":"session.next.reasoning.ended",
             "data":{"timestamp":1,"sessionID":"ses_1","text":"considering the options"}}
            """);

        Assert.IsType<ThoughtChunkEvent>(Assert.Single(events));
    }

    [Fact]
    public void A_failed_step_is_reported_as_an_error()
    {
        // The whole reason this adapter exists: over ACP this arrives as a clean end_turn.
        var events = Map("""
            {"type":"session.next.step.failed",
             "data":{"timestamp":1,"sessionID":"ses_1","assistantMessageID":"msg_1",
                     "error":{"message":"provider returned 529"}}}
            """);

        var error = Assert.IsType<AgentErrorEvent>(Assert.Single(events));
        Assert.Contains("529", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retry_is_surfaced_with_its_attempt_number()
    {
        // ACP has no equivalent at all — a retrying agent simply looked slow.
        var events = Map("""
            {"type":"session.next.retried",
             "data":{"timestamp":1,"sessionID":"ses_1","attempt":2,"error":{"message":"timeout"}}}
            """);

        var notice = Assert.IsType<NoticeEvent>(Assert.Single(events));
        Assert.Contains("attempt 2", notice.Message, StringComparison.Ordinal);
        Assert.Contains("timeout", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_success_and_failure_are_distinct_outcomes()
    {
        // ACP flattened both into "completed", so a failed tool looked like a successful one.
        var success = Assert.IsType<ToolCallUpdateEvent>(Assert.Single(Map("""
            {"type":"session.next.tool.success","data":{"timestamp":1,"sessionID":"ses_1","callID":"c1"}}
            """)));
        var failure = Assert.IsType<ToolCallUpdateEvent>(Assert.Single(Map("""
            {"type":"session.next.tool.failed","data":{"timestamp":1,"sessionID":"ses_1","callID":"c1",
             "error":{"message":"exit 1"}}}
            """)));

        Assert.Equal(ToolCallStatus.Completed, success.Status);
        Assert.Equal(ToolCallStatus.Failed, failure.Status);
    }

    [Fact]
    public void A_tool_call_carries_its_id_and_a_kind()
    {
        var call = Assert.IsType<ToolCallEvent>(Assert.Single(Map("""
            {"type":"session.next.tool.called",
             "data":{"timestamp":1,"sessionID":"ses_1","callID":"c1","tool":"bash","input":{}}}
            """)));

        Assert.Equal("c1", call.ToolCallId);
        Assert.Equal(ToolKind.Execute, call.Kind);
        Assert.Equal(ToolCallStatus.InProgress, call.Status);
    }

    [Theory]
    [InlineData("read", ToolKind.Read)]
    [InlineData("edit", ToolKind.Edit)]
    [InlineData("write", ToolKind.Edit)]
    [InlineData("grep", ToolKind.Search)]
    [InlineData("bash", ToolKind.Execute)]
    [InlineData("webfetch", ToolKind.Fetch)]
    // "task" hands work to a subagent; it is not the agent thinking, and calling it Think put
    // "Think task" on every delegation row in the transcript.
    [InlineData("task", ToolKind.Subagent)]
    [InlineData("something-new", ToolKind.Other)]
    public void Tool_names_map_to_kinds(string tool, ToolKind expected)
        => Assert.Equal(expected, OpenCodeEventMap.ToolKindFor(tool));

    [Fact]
    public void An_error_with_no_message_still_produces_something_readable()
    {
        // Half a description beats reporting nothing, which is what the ACP path did.
        var events = Map("""
            {"type":"session.next.step.failed",
             "data":{"timestamp":1,"sessionID":"ses_1","assistantMessageID":"msg_1","error":{"code":529}}}
            """);

        Assert.NotEmpty(Assert.IsType<AgentErrorEvent>(Assert.Single(events)).Message);
    }

    [Fact]
    public void Unmodelled_and_malformed_payloads_yield_nothing_rather_than_throwing()
    {
        Assert.Empty(Map("""{"type":"session.next.something.invented","data":{}}"""));
        Assert.Empty(Map("""{"data":{}}"""));       // no type
        Assert.Empty(Map("""{"type":"session.next.text.ended"}""")); // no data
        Assert.Empty(Map("[]"));                    // not an object
    }

    [Fact]
    public void Every_event_type_the_server_documents_is_accounted_for()
    {
        // Either mapped or deliberately omitted — the point is that none is a silent surprise, which is how
        // the ACP path lost usage reporting for an entire session.
        string[] documented =
        [
            "session.next.agent.switched", "session.next.model.switched", "session.next.moved",
            "session.next.prompted", "session.next.prompt.admitted", "session.next.context.updated",
            "session.next.synthetic", "session.next.shell.started", "session.next.shell.ended",
            "session.next.step.started", "session.next.step.ended", "session.next.step.failed",
            "session.next.text.started", "session.next.text.ended",
            "session.next.tool.input.started", "session.next.tool.input.ended",
            "session.next.tool.called", "session.next.tool.progress",
            "session.next.tool.success", "session.next.tool.failed",
            "session.next.reasoning.started", "session.next.reasoning.ended",
            "session.next.retried", "session.next.compaction.started", "session.next.compaction.ended",
            "session.next.revert.staged", "session.next.revert.cleared", "session.next.revert.committed",
        ];

        Assert.All(documented, t => Assert.True(OpenCodeEventMap.IsKnown(t), $"unaccounted event type: {t}"));
        Assert.False(OpenCodeEventMap.IsKnown("session.next.invented.later"));
    }

    // ---- the sandboxed path ----

    /// <summary>A sandbox that can't forward a port — the native adapter must refuse it rather than
    /// silently starting a server nothing can reach.</summary>
    private sealed class NoForwardSandbox : ISandboxCommand
    {
        public (string Command, IReadOnlyList<string> Arguments) WrapCommand(
            string command, IReadOnlyList<string> arguments, string workingDirectory)
            => (command, arguments);
    }

    [Fact]
    public async Task A_sandbox_that_cannot_forward_a_port_is_refused_with_a_reason()
    {
        var adapter = Agnes.Agents.OpenCode.Native.OpenCodeNativeAgent.Create(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => adapter.StartSessionAsync(
            new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), Sandbox = new NoForwardSandbox() }));

        // Names the actual constraint and the way out, rather than "not supported".
        Assert.Contains("forward a port", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ACP", error.Message, StringComparison.Ordinal);
    }
}
