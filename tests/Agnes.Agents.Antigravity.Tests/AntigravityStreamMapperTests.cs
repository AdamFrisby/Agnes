using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Antigravity;

namespace Agnes.Agents.Antigravity.Tests;

/// <summary>
/// The mapper, against frames captured verbatim from <c>agy 1.1.24</c> — not from documentation, which
/// does not exist for this protocol, and not from Claude's stream-json, which it resembles and is not.
/// </summary>
public sealed class AntigravityStreamMapperTests
{
    private static readonly AntigravityStreamMapper Mapper = new();

    private static IReadOnlyList<SessionEvent> Map(string json)
        => [.. Mapper.ToEvents(JsonDocument.Parse(json).RootElement)];

    [Fact]
    public void Init_announces_the_conversation_id_that_resume_will_need()
    {
        var events = Map("""
            {"event":"init","conversation_id":"5eed2404-41c2-409b-84c9-71ec54b7457e",
             "init":{"model":"gemini-3.8-flash-low","cwd":"/work","tools":["run_command","view_file"]}}
            """);

        var started = Assert.IsType<SessionStartedEvent>(Assert.Single(events));
        Assert.Equal("5eed2404-41c2-409b-84c9-71ec54b7457e", started.AgentSessionId);
    }

    [Fact]
    public void Agent_response_text_becomes_an_assistant_chunk()
    {
        var events = Map("""
            {"event":"step_update","step_update":{"conversation_id":"c","step_index":45,"state":"DONE",
             "step_type":"agent_response","text_delta":"XYZZY\n","duration_seconds":1.1}}
            """);

        var chunk = Assert.IsType<MessageChunkEvent>(Assert.Single(events));
        Assert.Equal(MessageRole.Assistant, chunk.Role);
        Assert.Equal("XYZZY\n", Assert.IsType<TextContent>(chunk.Content).Text);
    }

    [Fact]
    public void A_tool_call_opens_on_ACTIVE_and_closes_on_DONE_under_one_id()
    {
        // step_index is stable across both frames, which is what makes it usable as the call id — the
        // DONE frame is an update to the same call, not a second call.
        const string active = """
            {"event":"step_update","step_update":{"conversation_id":"c","step_index":2,"state":"ACTIVE",
             "step_type":"tool","tool_name":"find_by_name",
             "tool_info":{"name":"find_by_name","parameters":{"Pattern":"*secret.txt*","SearchDirectory":"/home"}}}}
            """;
        const string done = """
            {"event":"step_update","step_update":{"conversation_id":"c","step_index":2,"state":"DONE",
             "step_type":"tool","tool_name":"find_by_name","duration_seconds":8.09}}
            """;

        var call = Assert.IsType<ToolCallEvent>(Assert.Single(Map(active)));
        Assert.Equal("2", call.ToolCallId);
        Assert.Equal("find_by_name", call.Title);
        Assert.Equal(ToolKind.Search, call.Kind);
        Assert.Equal(ToolCallStatus.InProgress, call.Status);
        Assert.Contains("*secret.txt*", Assert.IsType<TextContent>(Assert.Single(call.Content)).Text);

        var update = Assert.IsType<ToolCallUpdateEvent>(Assert.Single(Map(done)));
        Assert.Equal("2", update.ToolCallId);
        Assert.Equal(ToolCallStatus.Completed, update.Status);
    }

    [Fact]
    public void A_tool_that_ends_in_ERROR_is_reported_failed_not_completed()
    {
        // Observed live: run_command steps do end in ERROR, and calling that "completed" would put a
        // green tick on a command that failed.
        var events = Map("""
            {"event":"step_update","step_update":{"conversation_id":"c","step_index":31,"state":"ERROR",
             "step_type":"tool","tool_name":"run_command","duration_seconds":8.05}}
            """);

        Assert.Equal(ToolCallStatus.Failed, Assert.IsType<ToolCallUpdateEvent>(Assert.Single(events)).Status);
    }

    [Fact]
    public void User_input_and_system_message_steps_produce_nothing()
    {
        // user_input only echoes the prompt Agnes already recorded; system_message carries no payload.
        Assert.Empty(Map("""
            {"event":"step_update","step_update":{"conversation_id":"c","step_index":0,"state":"DONE","step_type":"user_input"}}
            """));
        Assert.Empty(Map("""
            {"event":"step_update","step_update":{"conversation_id":"c","step_index":17,"state":"DONE","step_type":"system_message","duration_seconds":0.0001}}
            """));
    }

    [Fact]
    public void A_result_ends_the_turn_and_reports_usage()
    {
        var events = Map("""
            {"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"XYZZY",
             "duration_seconds":1.1,"num_turns":2,
             "usage":{"input_tokens":120,"output_tokens":8,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":128}}}
            """);

        var usage = Assert.IsType<UsageReportedEvent>(events[0]);
        Assert.Equal(120, usage.Metrics.InputTokens);
        Assert.Equal(8, usage.Metrics.OutputTokens);
        // Antigravity bills through a subscription and reports no cost; inventing one from tokens would
        // put a fabricated number in the spend column.
        Assert.Null(usage.Metrics.CostUsd);

        Assert.Equal(StopReason.EndTurn, Assert.IsType<TurnEndedEvent>(events[1]).Reason);
    }

    [Fact]
    public void An_error_result_surfaces_the_message_and_refuses_the_turn()
    {
        var events = Map("""
            {"event":"result","result":{"conversation_id":"c","status":"ERROR","response":"",
             "error":"stream input message is missing the \"event\" field","num_turns":0}}
            """);

        Assert.Contains(events, e => e is AgentErrorEvent err && err.Message.Contains("missing the"));
        Assert.Equal(StopReason.Refusal, Assert.IsType<TurnEndedEvent>(events[^1]).Reason);
    }

    [Fact]
    public void An_unknown_frame_is_ignored_rather_than_throwing()
    {
        // The CLI is proprietary and versions independently; a frame kind Agnes has never seen is the
        // expected outcome of an update, not a fault.
        Assert.Empty(Map("""{"event":"something_new","payload":{"x":1}}"""));
        Assert.Empty(Map("""{"no_event_key":true}"""));
    }

    [Fact]
    public void The_user_turn_is_event_keyed_which_is_the_whole_difference_from_Claude()
    {
        // Feeding agy a Claude-shaped {"type":"user",...} line answers:
        // "stream input message is missing the \"event\" field".
        var line = Mapper.BuildUserTurn([new TextContent("say HELLO_AGY")]);
        using var parsed = JsonDocument.Parse(line);

        Assert.Equal("user", parsed.RootElement.GetProperty("event").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("type", out _));

        var message = parsed.RootElement.GetProperty("message");
        Assert.Equal("user", message.GetProperty("role").GetString());
        var block = message.GetProperty("content")[0];
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("say HELLO_AGY", block.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_skip_permissions_flag_is_always_passed(bool requested)
    {
        // Not a convenience. Without it agy does not prompt — it redirects writes to a scratch directory
        // and reports success. The adapter refuses non-autonomous sessions outright; if one ever reached
        // here, launching it unflagged would be the silently-wrong outcome rather than the safe one.
        Assert.Equal(["--dangerously-skip-permissions"], Mapper.PermissionLaunchArguments(requested));
    }

    [Fact]
    public void There_is_no_permission_response_to_build()
        => Assert.Null(Mapper.BuildPermissionResponse("req-1", allow: true));
}
