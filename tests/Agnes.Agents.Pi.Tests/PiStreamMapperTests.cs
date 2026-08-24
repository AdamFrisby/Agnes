using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Pi;

namespace Agnes.Agents.Pi.Tests;

/// <summary>
/// Golden tests over Pi's RPC protocol, line by line. Shapes are taken verbatim from the <c>docs/rpc.md</c>
/// that ships with pi-coding-agent v0.84.3 and from a live <c>pi --mode rpc</c> handshake.
/// </summary>
public sealed class PiStreamMapperTests
{
    private static IReadOnlyList<SessionEvent> Map(PiStreamMapper mapper, string line)
    {
        using var doc = JsonDocument.Parse(line);
        return mapper.ToEvents(doc.RootElement.Clone()).ToList();
    }

    private static IReadOnlyList<SessionEvent> Map(string line) => Map(new PiStreamMapper(), line);

    // ---- the retry contract: the whole reason this adapter exists ----

    [Fact]
    public void A_turn_ends_on_agent_settled_not_on_agent_end()
    {
        var mapper = new PiStreamMapper();

        // agent_end fires per low-level run and can be followed by a retry, so it must NOT end Agnes's turn:
        // reporting it as the end would present a transient provider failure as a finished turn.
        Assert.Empty(Map(mapper, """{"type":"agent_end","messages":[],"willRetry":true}"""));
        Assert.Empty(Map(mapper, """{"type":"turn_end","message":{},"toolResults":[]}"""));

        var settled = Map(mapper, """{"type":"agent_settled"}""");

        Assert.Equal(StopReason.EndTurn, Assert.IsType<TurnEndedEvent>(Assert.Single(settled)).Reason);
    }

    [Fact]
    public void A_retry_is_announced_rather_than_hidden()
    {
        var events = Map(
            """
            {"type":"auto_retry_start","attempt":1,"maxAttempts":3,"delayMs":2000,
             "errorMessage":"Provider finish_reason: network_error"}
            """);

        // A silent multi-second backoff is indistinguishable from a hang, which is exactly the failure this
        // adapter is meant to make legible.
        var notice = Assert.IsType<NoticeEvent>(Assert.Single(events));
        Assert.False(notice.IsError);
        Assert.Contains("attempt 1 of 3", notice.Message, StringComparison.Ordinal);
        Assert.Contains("2s", notice.Message, StringComparison.Ordinal);
        Assert.Contains("network_error", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recovered_retry_is_a_notice_and_an_exhausted_one_is_an_error()
    {
        var recovered = Map("""{"type":"auto_retry_end","success":true,"attempt":2}""");
        Assert.IsType<NoticeEvent>(Assert.Single(recovered));

        var exhausted = Map(
            """{"type":"auto_retry_end","success":false,"attempt":3,"finalError":"529 overloaded_error"}""");

        // Retries exhausted is the one place the turn genuinely failed, and the only place to say so.
        var error = Assert.IsType<AgentErrorEvent>(Assert.Single(exhausted));
        Assert.Contains("529 overloaded_error", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_compaction_retry_is_reported_too()
    {
        var events = Map(
            """
            {"type":"summarization_retry_scheduled","attempt":1,"maxAttempts":3,"delayMs":2000,
             "errorMessage":"terminated"}
            """);

        Assert.Contains("Compaction", Assert.IsType<NoticeEvent>(Assert.Single(events)).Message, StringComparison.Ordinal);
    }

    // ---- streaming ----

    [Fact]
    public void Text_deltas_become_assistant_message_chunks()
    {
        var events = Map(
            """
            {"type":"message_update","usage":{},"assistantMessageEvent":
             {"type":"text_delta","contentIndex":0,"delta":"Hello "}}
            """);

        var chunk = Assert.IsType<MessageChunkEvent>(Assert.Single(events));
        Assert.Equal(MessageRole.Assistant, chunk.Role);
        Assert.Equal("Hello ", Assert.IsType<TextContent>(chunk.Content).Text);
    }

    [Fact]
    public void Thinking_deltas_become_thought_chunks()
    {
        var events = Map(
            """
            {"type":"message_update","usage":{},"assistantMessageEvent":
             {"type":"thinking_delta","contentIndex":0,"delta":"considering…"}}
            """);

        Assert.Equal("considering…", Assert.IsType<TextContent>(
            Assert.IsType<ThoughtChunkEvent>(Assert.Single(events)).Content).Text);
    }

    [Fact]
    public void Text_start_and_end_produce_nothing_so_the_transcript_isnt_doubled()
    {
        // text_end repeats the whole accumulated content; emitting it too would duplicate every reply.
        Assert.Empty(Map("""{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_start","contentIndex":0}}"""));
        Assert.Empty(Map("""{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_end","contentIndex":0,"content":"Hello world"}}"""));
    }

    // ---- tools ----

    [Fact]
    public void A_tool_call_runs_from_start_through_update_to_completion()
    {
        var mapper = new PiStreamMapper();

        var start = Map(mapper, """
            {"type":"tool_execution_start","toolCallId":"call_abc","toolName":"bash","args":{"command":"ls -la"}}
            """);
        var call = Assert.IsType<ToolCallEvent>(Assert.Single(start));
        Assert.Equal("call_abc", call.ToolCallId);
        Assert.Equal("bash", call.Title);
        Assert.Equal(ToolKind.Execute, call.Kind);
        Assert.Equal(ToolCallStatus.InProgress, call.Status);
        // The tool's input is shown verbatim: an approval or audit trail that clips a shell command is
        // exactly where a dangerous tail hides.
        Assert.Contains("ls -la", Assert.IsType<TextContent>(Assert.Single(call.Content)).Text, StringComparison.Ordinal);

        var update = Map(mapper, """
            {"type":"tool_execution_update","toolCallId":"call_abc","toolName":"bash",
             "partialResult":{"content":[{"type":"text","text":"total 48"}]}}
            """);
        Assert.Equal(ToolCallStatus.InProgress, Assert.IsType<ToolCallUpdateEvent>(Assert.Single(update)).Status);

        var end = Map(mapper, """
            {"type":"tool_execution_end","toolCallId":"call_abc","toolName":"bash",
             "result":{"content":[{"type":"text","text":"total 48\ndrwxr-xr-x"}]},"isError":false}
            """);
        var done = Assert.IsType<ToolCallUpdateEvent>(Assert.Single(end));
        Assert.Equal(ToolCallStatus.Completed, done.Status);
        Assert.Contains("drwxr-xr-x", Assert.IsType<TextContent>(Assert.Single(done.Content!)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_tool_is_marked_failed()
    {
        var end = Map("""
            {"type":"tool_execution_end","toolCallId":"c1","toolName":"read",
             "result":{"content":[{"type":"text","text":"ENOENT"}]},"isError":true}
            """);

        Assert.Equal(ToolCallStatus.Failed, Assert.IsType<ToolCallUpdateEvent>(Assert.Single(end)).Status);
    }

    [Theory]
    [InlineData("read", ToolKind.Read)]
    [InlineData("edit", ToolKind.Edit)]
    [InlineData("write", ToolKind.Edit)]
    [InlineData("bash", ToolKind.Execute)]
    [InlineData("powershell", ToolKind.Execute)]
    [InlineData("grep", ToolKind.Search)]
    [InlineData("find", ToolKind.Search)]
    [InlineData("ls", ToolKind.Search)]
    [InlineData("some_extension_tool", ToolKind.Other)]
    public void Pis_builtin_tools_map_onto_the_cross_adapter_taxonomy(string name, ToolKind expected)
        => Assert.Equal(expected, PiStreamMapper.ToKind(name));

    // ---- session identity, usage, faults ----

    [Fact]
    public void The_handshake_reply_supplies_the_session_id_agnes_resumes_with()
    {
        // Captured live: pi --mode rpc announces nothing at startup and answers get_state with its id.
        var events = Map("""
            {"id":"agnes-handshake","type":"response","command":"get_state","success":true,
             "data":{"sessionId":"01a034ae-8f56-703b-97e8-6900c2212f33","messageCount":0}}
            """);

        var started = Assert.IsType<SessionStartedEvent>(Assert.Single(events));
        // Agnes only resumes an id that looks real (a dashed UUID), so the shape matters, not just the value.
        Assert.Contains("-", started.AgentSessionId, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_command_surfaces_as_an_error()
    {
        var events = Map("""
            {"type":"response","command":"set_model","success":false,"error":"Model not found: invalid/model"}
            """);

        Assert.Contains("Model not found", Assert.IsType<AgentErrorEvent>(Assert.Single(events)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_assistant_message_reports_usage()
    {
        var events = Map("""
            {"type":"message_end","message":{"role":"assistant","stopReason":"stop",
             "usage":{"input":100,"output":50,"cacheRead":10,"cacheWrite":0,
                      "cost":{"input":0.0003,"output":0.00075,"total":0.00105}}}}
            """);

        var usage = Assert.IsType<UsageReportedEvent>(Assert.Single(events)).Metrics;
        Assert.Equal(110, usage.ContextUsed);
        Assert.Equal(50, usage.OutputTokens);
        Assert.Equal(0.00105, usage.CostUsd);
    }

    [Fact]
    public void The_stop_reason_of_the_last_message_reaches_the_turn_that_settles()
    {
        var mapper = new PiStreamMapper();

        Map(mapper, """{"type":"message_end","message":{"role":"assistant","stopReason":"aborted"}}""");
        var settled = Map(mapper, """{"type":"agent_settled"}""");

        // agent_settled states no reason of its own, so it has to be carried forward — otherwise a
        // cancelled turn is indistinguishable from a clean one.
        var ended = Assert.IsType<TurnEndedEvent>(Assert.Single(settled));
        Assert.Equal(StopReason.Cancelled, ended.Reason);
        Assert.Equal("aborted", ended.RawReason);
    }

    [Fact]
    public void An_unknown_stop_reason_is_narrowed_but_kept_verbatim()
    {
        var mapper = new PiStreamMapper();

        Map(mapper, """{"type":"message_end","message":{"role":"assistant","stopReason":"something_new"}}""");
        var ended = Assert.IsType<TurnEndedEvent>(Assert.Single(Map(mapper, """{"type":"agent_settled"}""")));

        Assert.Equal(StopReason.EndTurn, ended.Reason);
        Assert.Equal("something_new", ended.RawReason);
    }

    [Fact]
    public void Unknown_lines_are_ignored_rather_than_failing_the_stream()
    {
        // Pi adds events between releases; an adapter that threw on one would take the session down.
        Assert.Empty(Map("""{"type":"queue_update","steering":[],"followUp":[]}"""));
        Assert.Empty(Map("""{"type":"something_pi_added_last_week","payload":{}}"""));
        Assert.Empty(Map("""{}"""));
    }

    // ---- outbound ----

    [Fact]
    public void The_handshake_asks_for_state_so_the_session_id_is_known_before_the_first_prompt()
    {
        var handshake = Assert.Single(new PiStreamMapper().Handshake());

        using var doc = JsonDocument.Parse(handshake);
        Assert.Equal("get_state", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(PiStreamMapper.HandshakeRequestId, doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Cancelling_sends_pis_abort_command()
    {
        var cancel = new PiStreamMapper().BuildCancel();

        Assert.NotNull(cancel);
        using var doc = JsonDocument.Parse(cancel);
        Assert.Equal("abort", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void A_user_turn_is_a_prompt_command_with_no_streaming_behaviour()
    {
        var line = new PiStreamMapper().BuildUserTurn([new TextContent("fix the build")]);

        using var doc = JsonDocument.Parse(line);
        Assert.Equal("prompt", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("fix the build", doc.RootElement.GetProperty("message").GetString());
        // Agnes serialises turns itself, so a prompt only arrives when Pi is idle; declaring a queueing
        // behaviour would change when a message the user expects to run now actually runs.
        Assert.False(doc.RootElement.TryGetProperty("streamingBehavior", out _));
        Assert.False(doc.RootElement.TryGetProperty("images", out _));
    }

    [Fact]
    public void Images_ride_along_in_pis_own_content_shape()
    {
        var line = new PiStreamMapper().BuildUserTurn(
            [new TextContent("what is this?"), new ImageContent("image/png", "aGk=")]);

        using var doc = JsonDocument.Parse(line);
        var image = Assert.Single(doc.RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal("image", image.GetProperty("type").GetString());
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        Assert.Equal("aGk=", image.GetProperty("data").GetString());
    }

    [Fact]
    public void There_are_no_permission_flags_because_pi_has_no_permission_system()
    {
        var mapper = new PiStreamMapper();

        Assert.Empty(mapper.PermissionLaunchArguments(skipPermissions: false));
        Assert.Empty(mapper.PermissionLaunchArguments(skipPermissions: true));
        // ...and nothing to answer with, which is why an attended session is refused at launch instead.
        Assert.Null(mapper.BuildPermissionResponse("req-1", allow: true));
    }
}
