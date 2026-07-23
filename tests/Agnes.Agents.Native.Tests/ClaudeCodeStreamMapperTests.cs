using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Native;

namespace Agnes.Agents.Native.Tests;

public class ClaudeCodeStreamMapperTests
{
    private static List<SessionEvent> Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ClaudeCodeStreamMapper().ToEvents(doc.RootElement).ToList();
    }

    // Runs several lines through the SAME mapper (needed for state learned from init, e.g. the model window).
    private static List<SessionEvent> MapAll(params string[] jsonLines)
    {
        var mapper = new ClaudeCodeStreamMapper();
        var events = new List<SessionEvent>();
        foreach (var json in jsonLines)
        {
            using var doc = JsonDocument.Parse(json);
            events.AddRange(mapper.ToEvents(doc.RootElement));
        }

        return events;
    }

    [Fact]
    public void Init_starts_the_session()
    {
        var e = Map("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\"}");
        Assert.Equal("s1", Assert.IsType<SessionStartedEvent>(Assert.Single(e)).AgentSessionId);
    }

    [Fact]
    public void Assistant_text_becomes_a_message_chunk()
    {
        var e = Map("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hi\"}]}}");
        var m = Assert.IsType<MessageChunkEvent>(Assert.Single(e));
        Assert.Equal(MessageRole.Assistant, m.Role);
        Assert.Equal("Hi", ((TextContent)m.Content).Text);
    }

    [Fact]
    public void Tool_use_becomes_a_tool_call()
    {
        var e = Map("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"src/a.cs\"}}]}}");
        var tc = Assert.IsType<ToolCallEvent>(Assert.Single(e));
        Assert.Equal("t1", tc.ToolCallId);
        Assert.Equal(ToolKind.Read, tc.Kind);
        Assert.Contains("src/a.cs", tc.Title);
    }

    [Fact]
    public void Task_tool_becomes_a_subagent()
    {
        var e = Map("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"task1\",\"name\":\"Task\",\"input\":{\"description\":\"review the change\",\"subagent_type\":\"code-reviewer\"}}]}}");
        var sub = Assert.IsType<SubagentStartedEvent>(Assert.Single(e));
        Assert.Equal("task1", sub.SubagentId);
        Assert.Equal("review the change", sub.Name);
    }

    [Fact]
    public void Tool_result_updates_the_tool_call()
    {
        var e = Map("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"done\"}]}}");
        var u = Assert.IsType<ToolCallUpdateEvent>(Assert.Single(e));
        Assert.Equal("t1", u.ToolCallId);
        Assert.Equal(ToolCallStatus.Completed, u.Status);
    }

    [Fact]
    public void Result_ends_the_turn()
    {
        // A clean result is just a turn-end.
        Assert.Equal(StopReason.EndTurn, Assert.IsType<TurnEndedEvent>(Assert.Single(Map("{\"type\":\"result\",\"is_error\":false}"))).Reason);

        // An errored result surfaces the error message, then ends the turn as a refusal.
        var errored = Map("{\"type\":\"result\",\"is_error\":true,\"result\":\"API Error: 401 OAuth access token has been revoked\"}");
        Assert.Collection(errored,
            e => Assert.Equal("API Error: 401 OAuth access token has been revoked", Assert.IsType<AgentErrorEvent>(e).Message),
            e => Assert.Equal(StopReason.Refusal, Assert.IsType<TurnEndedEvent>(e).Reason));
    }

    [Fact]
    public void Assistant_usage_becomes_a_usage_event_with_summed_context_tokens()
    {
        // input + cache_read + cache_creation all count against the context window.
        var e = Map("{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":18000,\"cache_creation_input_tokens\":240,\"output_tokens\":120},\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");
        var usage = Assert.Single(e.OfType<UsageReportedEvent>());
        Assert.Equal(18_250, usage.Metrics.ContextUsed);
        Assert.Equal(120, usage.Metrics.OutputTokens);
        Assert.Null(usage.Metrics.ContextWindow); // no init seen → window unknown, not guessed
        Assert.Null(usage.Metrics.CostUsd);
    }

    [Fact]
    public void No_usage_block_emits_no_usage_event()
    {
        var e = Map("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");
        Assert.Empty(e.OfType<UsageReportedEvent>());
    }

    [Fact]
    public void Init_model_supplies_the_real_context_window()
    {
        var standard = MapAll(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\",\"model\":\"claude-sonnet-4-5\"}",
            "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":5000}}}");
        Assert.Equal(200_000, Assert.Single(standard.OfType<UsageReportedEvent>()).Metrics.ContextWindow);

        var longContext = MapAll(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\",\"model\":\"claude-opus-4-8[1m]\"}",
            "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":5000}}}");
        Assert.Equal(1_000_000, Assert.Single(longContext.OfType<UsageReportedEvent>()).Metrics.ContextWindow);
    }

    [Fact]
    public void Result_cost_becomes_a_usage_event_before_the_turn_ends()
    {
        var e = Map("{\"type\":\"result\",\"is_error\":false,\"total_cost_usd\":0.0345}");
        Assert.Equal(0.0345, Assert.Single(e.OfType<UsageReportedEvent>()).Metrics.CostUsd);
        // Usage is reported before the turn-ended marker.
        Assert.IsType<UsageReportedEvent>(e[0]);
        Assert.IsType<TurnEndedEvent>(e[1]);
    }

    [Fact]
    public void Build_user_turn_is_valid_stream_json()
    {
        var line = new ClaudeCodeStreamMapper().BuildUserTurn([new TextContent("hello there")]);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
        var text = doc.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("hello there", text);
    }

    [Fact]
    public void Can_use_tool_control_request_becomes_a_permission_request()
    {
        var e = Map("""
            {"type":"control_request","request_id":"req-9","request":{"subtype":"can_use_tool","tool_name":"Write","tool_use_id":"tu-1","input":{"file_path":"/work/a.txt"}}}
            """);
        var perm = Assert.IsType<PermissionRequestedEvent>(Assert.Single(e));
        Assert.Equal("req-9", perm.RequestId);
        Assert.Equal("tu-1", perm.ToolCallId);
        Assert.Contains("Write", perm.Title);
        Assert.Contains(perm.Options, o => o.OptionId == "allow");
        Assert.Contains(perm.Options, o => o.OptionId == "reject");
    }

    [Fact]
    public void Permission_launch_arguments_default_to_asking_and_skip_when_opted_in()
    {
        var mapper = new ClaudeCodeStreamMapper();
        Assert.Equal(["--permission-prompt-tool", "stdio"], mapper.PermissionLaunchArguments(skipPermissions: false));
        Assert.Equal(["--dangerously-skip-permissions"], mapper.PermissionLaunchArguments(skipPermissions: true));
    }

    [Theory]
    [InlineData(true, "allow")]
    [InlineData(false, "deny")]
    public void Permission_response_is_valid_control_response(bool allow, string behavior)
    {
        var line = new ClaudeCodeStreamMapper().BuildPermissionResponse("req-9", allow);
        using var doc = JsonDocument.Parse(line!);
        Assert.Equal("control_response", doc.RootElement.GetProperty("type").GetString());
        var response = doc.RootElement.GetProperty("response");
        Assert.Equal("req-9", response.GetProperty("request_id").GetString());
        Assert.Equal(behavior, response.GetProperty("response").GetProperty("behavior").GetString());
    }

    private const string AskQuestion =
        "{\"type\":\"control_request\",\"request_id\":\"r1\",\"request\":{\"subtype\":\"can_use_tool\","
        + "\"tool_name\":\"AskUserQuestion\",\"tool_use_id\":\"tu1\",\"input\":{\"questions\":["
        + "{\"question\":\"Which db?\",\"header\":\"DB\",\"multiSelect\":false,"
        + "\"options\":[{\"label\":\"SQLite\",\"description\":\"local\"},{\"label\":\"Postgres\",\"description\":\"server\"}]}]}}}";

    [Fact]
    public void AskUserQuestion_control_request_becomes_a_question_event()
    {
        var mapper = new ClaudeCodeStreamMapper();
        using var doc = JsonDocument.Parse(AskQuestion);
        var q = Assert.IsType<QuestionAskedEvent>(Assert.Single(mapper.ToEvents(doc.RootElement)));

        Assert.Equal("r1", q.RequestId);
        var question = Assert.Single(q.Questions);
        Assert.Equal("DB", question.Header);
        Assert.Equal("Which db?", question.Prompt);
        Assert.False(question.MultiSelect);
        Assert.Equal(["SQLite", "Postgres"], question.Options.Select(o => o.Label));
    }

    [Fact]
    public void A_non_question_tool_still_maps_to_a_permission_request()
    {
        var mapper = new ClaudeCodeStreamMapper();
        var line = "{\"type\":\"control_request\",\"request_id\":\"r0\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\"}}";
        using var doc = JsonDocument.Parse(line);
        Assert.IsType<PermissionRequestedEvent>(Assert.Single(mapper.ToEvents(doc.RootElement)));
    }

    [Fact]
    public void Answering_a_question_echoes_the_input_and_carries_selections_and_notes()
    {
        var mapper = new ClaudeCodeStreamMapper();
        using (var doc = JsonDocument.Parse(AskQuestion)) { _ = mapper.ToEvents(doc.RootElement).ToList(); }

        var line = mapper.BuildQuestionResponse("r1", [new QuestionAnswer("Which db?", ["SQLite"], "prefer zero-config")]);
        Assert.NotNull(line);
        using var resp = JsonDocument.Parse(line!);
        var inner = resp.RootElement.GetProperty("response").GetProperty("response");
        Assert.Equal("allow", inner.GetProperty("behavior").GetString());
        var updated = inner.GetProperty("updatedInput");
        Assert.True(updated.TryGetProperty("questions", out _)); // original questions echoed back
        Assert.Equal("SQLite", updated.GetProperty("answers").GetProperty("Which db?").GetString());
        Assert.Equal("prefer zero-config",
            updated.GetProperty("questionStates").GetProperty("Which db?").GetProperty("textInputValue").GetString());

        // The request is single-use — a second response is null.
        Assert.Null(mapper.BuildQuestionResponse("r1", []));
    }

    [Fact]
    public void Multi_select_answers_are_comma_joined()
    {
        var mapper = new ClaudeCodeStreamMapper();
        var ask = AskQuestion.Replace("\"multiSelect\":false", "\"multiSelect\":true");
        using (var doc = JsonDocument.Parse(ask)) { _ = mapper.ToEvents(doc.RootElement).ToList(); }

        var line = mapper.BuildQuestionResponse("r1", [new QuestionAnswer("Which db?", ["SQLite", "Postgres"], null)]);
        using var resp = JsonDocument.Parse(line!);
        Assert.Equal("SQLite, Postgres", resp.RootElement.GetProperty("response").GetProperty("response")
            .GetProperty("updatedInput").GetProperty("answers").GetProperty("Which db?").GetString());
    }

    [Fact]
    public void Dismissing_a_question_denies()
    {
        var mapper = new ClaudeCodeStreamMapper();
        using (var doc = JsonDocument.Parse(AskQuestion)) { _ = mapper.ToEvents(doc.RootElement).ToList(); }

        var line = mapper.BuildQuestionResponse("r1", []);
        using var resp = JsonDocument.Parse(line!);
        Assert.Equal("deny", resp.RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
    }
}
