using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Codex;

namespace Agnes.Agents.Codex.Tests;

public class CodexMapTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static List<SessionEvent> Started(CodexMap map, string itemJson)
        => map.ItemStarted(J($"{{\"item\":{itemJson},\"threadId\":\"t1\",\"turnId\":\"u1\"}}")).ToList();

    private static List<SessionEvent> Completed(CodexMap map, string itemJson)
        => map.ItemCompleted(J($"{{\"item\":{itemJson},\"threadId\":\"t1\",\"turnId\":\"u1\"}}")).ToList();

    [Fact]
    public void Agent_message_delta_becomes_an_assistant_chunk()
    {
        var map = new CodexMap();
        var e = map.AgentMessageDelta(J("{\"delta\":\"Hel\",\"itemId\":\"m1\",\"threadId\":\"t1\",\"turnId\":\"u1\"}"));
        var m = Assert.IsType<MessageChunkEvent>(e);
        Assert.Equal(MessageRole.Assistant, m.Role);
        Assert.Equal("Hel", ((TextContent)m.Content).Text);
    }

    [Fact]
    public void Completed_agent_message_is_suppressed_if_it_already_streamed()
    {
        var map = new CodexMap();
        map.AgentMessageDelta(J("{\"delta\":\"Hi\",\"itemId\":\"m1\",\"threadId\":\"t1\",\"turnId\":\"u1\"}"));
        // The final item/completed carries the whole text again — don't double it.
        var events = Completed(map, "{\"type\":\"agentMessage\",\"id\":\"m1\",\"text\":\"Hi\"}");
        Assert.Empty(events);
    }

    [Fact]
    public void Completed_agent_message_is_emitted_when_no_deltas_streamed()
    {
        var map = new CodexMap();
        var events = Completed(map, "{\"type\":\"agentMessage\",\"id\":\"m2\",\"text\":\"Done.\"}");
        var m = Assert.IsType<MessageChunkEvent>(Assert.Single(events));
        Assert.Equal("Done.", ((TextContent)m.Content).Text);
    }

    [Fact]
    public void Reasoning_becomes_a_thought()
    {
        var map = new CodexMap();
        var events = Completed(map, "{\"type\":\"reasoning\",\"id\":\"r1\",\"summary\":\"Thinking about it\"}");
        Assert.IsType<ThoughtChunkEvent>(Assert.Single(events));
    }

    [Fact]
    public void User_message_is_not_echoed_back()
    {
        var map = new CodexMap();
        var events = Completed(map, "{\"type\":\"userMessage\",\"id\":\"x\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}");
        Assert.Empty(events); // the host records the user's prompt itself
    }

    [Fact]
    public void Command_execution_starts_a_tool_call_then_updates_it()
    {
        var map = new CodexMap();
        var start = Started(map, "{\"type\":\"commandExecution\",\"id\":\"c1\",\"command\":[\"ls\",\"-la\"],\"status\":\"inProgress\"}");
        var tc = Assert.IsType<ToolCallEvent>(Assert.Single(start));
        Assert.Equal("c1", tc.ToolCallId);
        Assert.Equal(ToolKind.Execute, tc.Kind);
        Assert.Equal(ToolCallStatus.InProgress, tc.Status);
        Assert.Contains("ls -la", tc.Title);

        var done = Completed(map, "{\"type\":\"commandExecution\",\"id\":\"c1\",\"command\":[\"ls\",\"-la\"],\"status\":\"completed\",\"aggregatedOutput\":\"file.txt\"}");
        var upd = Assert.IsType<ToolCallUpdateEvent>(Assert.Single(done));
        Assert.Equal("c1", upd.ToolCallId);
        Assert.Equal(ToolCallStatus.Completed, upd.Status);
    }

    [Fact]
    public void Completed_tool_without_a_start_is_surfaced_as_a_completed_call()
    {
        var map = new CodexMap();
        // No item/started seen for c9 → emit a full ToolCallEvent so the transcript isn't left empty.
        var done = Completed(map, "{\"type\":\"fileChange\",\"id\":\"c9\",\"status\":\"completed\",\"changes\":[{\"path\":\"src/a.cs\"}]}");
        var tc = Assert.IsType<ToolCallEvent>(Assert.Single(done));
        Assert.Equal(ToolKind.Edit, tc.Kind);
        Assert.Equal(ToolCallStatus.Completed, tc.Status);
        Assert.Contains("src/a.cs", tc.Title);
    }

    [Fact]
    public void Token_usage_becomes_real_usage_context_window_and_output()
    {
        var map = new CodexMap();
        var e = map.TokenUsage(J(
            "{\"threadId\":\"t1\",\"turnId\":\"u1\",\"tokenUsage\":{\"modelContextWindow\":272000," +
            "\"total\":{\"inputTokens\":1000,\"cachedInputTokens\":8000,\"outputTokens\":250,\"reasoningOutputTokens\":40,\"totalTokens\":9250}}}"));
        var u = Assert.IsType<UsageReportedEvent>(e);
        Assert.Equal(9000, u.ContextTokens); // input + cached
        Assert.Equal(272000, u.ContextWindow);
        Assert.Equal(250, u.OutputTokens);
        Assert.Null(u.CostUsd); // Codex reports no USD cost here — never fabricated
    }

    [Fact]
    public void Plan_text_becomes_plan_entries()
    {
        var map = new CodexMap();
        var events = Completed(map, "{\"type\":\"plan\",\"id\":\"p1\",\"text\":\"- [x] read files\\n- [ ] write test\"}");
        var plan = Assert.IsType<PlanEvent>(Assert.Single(events));
        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal("read files", plan.Entries[0].Content);
        Assert.Equal("completed", plan.Entries[0].Status);
        Assert.Equal("write test", plan.Entries[1].Content);
        Assert.Equal("pending", plan.Entries[1].Status);
    }

    [Fact]
    public void Input_maps_text_content_and_stop_reasons_and_decisions()
    {
        var input = CodexMap.ToInput([new TextContent("hello")]);
        var item = Assert.Single(input);
        Assert.Equal("text", item.Type);
        Assert.Equal("hello", item.Text);

        Assert.Equal(StopReason.EndTurn, CodexMap.ToStopReason("completed"));
        Assert.Equal(StopReason.Cancelled, CodexMap.ToStopReason("interrupted"));
        Assert.Equal(StopReason.Refusal, CodexMap.ToStopReason("failed"));

        Assert.Equal("approved", CodexMap.Decision(true));
        Assert.Equal("denied", CodexMap.Decision(false));
    }
}
