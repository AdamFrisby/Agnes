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
}
