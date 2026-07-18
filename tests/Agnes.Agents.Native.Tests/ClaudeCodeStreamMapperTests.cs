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
        Assert.Equal(StopReason.EndTurn, Assert.IsType<TurnEndedEvent>(Assert.Single(Map("{\"type\":\"result\",\"is_error\":false}"))).Reason);
        Assert.Equal(StopReason.Refusal, Assert.IsType<TurnEndedEvent>(Assert.Single(Map("{\"type\":\"result\",\"is_error\":true}"))).Reason);
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
}
