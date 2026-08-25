using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Acp;

namespace Agnes.Acp.Tests;

/// <summary>
/// Recognizing a subagent over ACP, which carries no notion of one. The tool's <em>name</em> — the field
/// every other adapter keys off — is not on the wire at all: ACP sends a title meant for a human, and
/// Copilot substitutes one, reporting its <c>task</c> tool as "Explore importer architecture" (or
/// "Running subtask", when the caller described nothing). Matching titles against known tool names
/// therefore never found a Copilot subagent, and none ever reached the agent roster.
/// </summary>
public class AcpSubagentTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static List<SessionEvent> Map(string update) => [.. AcpMap.ToEvents(Json(update))];

    // Verbatim from a real Copilot session's own event log (CLI v1.0.80).
    private const string CopilotTask = """
        {
          "sessionUpdate": "tool_call",
          "toolCallId": "tooluse_eoSCv01Me1YVbQiSNIKxEP",
          "title": "Explore extension spec format",
          "kind": "other",
          "status": "pending",
          "rawInput": {
            "agent_type": "explore",
            "description": "Explore extension spec format",
            "prompt": "I need to understand the format and conventions used in the extension specification files...",
            "mode": "background"
          }
        }
        """;

    [Fact]
    public void A_copilot_task_call_announces_a_subagent()
    {
        var events = Map(CopilotTask);

        // Still an ordinary tool row — that row is where the subagent's result comes back.
        var tool = Assert.IsType<ToolCallEvent>(events[0]);
        Assert.Equal("Explore extension spec format", tool.Title);
        Assert.Equal(ToolKind.Other, tool.Kind);   // Copilot maps `task` to ACP's "other"

        // …and now also a roster entry, keyed by the call that launched it.
        var sub = Assert.IsType<SubagentStartedEvent>(events[1]);
        Assert.Equal("tooluse_eoSCv01Me1YVbQiSNIKxEP", sub.SubagentId);
        Assert.Equal("Explore extension spec format", sub.Name);
    }

    [Fact]
    public void The_agent_type_names_it_when_the_caller_described_nothing()
    {
        var launch = AcpSubagentLaunch.TryParse(Json("""{"agent_type":"explore","prompt":"go and look"}"""))!;
        Assert.Equal("explore", launch.Name);
        Assert.Null(launch.Description);
        Assert.False(launch.IsBackground);
    }

    [Fact]
    public void Claudes_spelling_of_the_same_convention_is_recognized_too()
    {
        // Matched by shape, not by adapter: Claude's Task tool says subagent_type where Copilot says
        // agent_type, and an agent that adopts the convention needs no change here.
        var launch = AcpSubagentLaunch.TryParse(
            Json("""{"subagent_type":"code-reviewer","description":"Review the diff","prompt":"..."}"""))!;
        Assert.Equal("Review the diff", launch.Name);
        Assert.Equal("code-reviewer", launch.AgentType);
    }

    [Theory]
    // An ordinary tool call, carrying neither half of the convention.
    [InlineData("""{"path":"/work/src/a.cs"}""")]
    // A prompt alone is far too common to act on.
    [InlineData("""{"prompt":"summarize this file"}""")]
    // Copilot's read_agent/list_agents name an agent without dispatching to one — a roster row on every
    // glance at an agent definition would be worse than none.
    [InlineData("""{"agent_type":"explore"}""")]
    [InlineData("""{"agent_type":"explore","description":"Read the explore agent"}""")]
    public void An_ordinary_call_announces_nothing(string rawInput)
    {
        Assert.Null(AcpSubagentLaunch.TryParse(Json(rawInput)));

        var events = Map($$"""
            {"sessionUpdate":"tool_call","toolCallId":"tc1","title":"Viewing a.cs","kind":"read",
             "status":"pending","rawInput":{{rawInput}}}
            """);
        Assert.IsType<ToolCallEvent>(Assert.Single(events));
    }

    [Fact]
    public void A_call_with_no_raw_input_at_all_is_unaffected()
    {
        // Not every agent sends rawInput, and the absence must cost nothing.
        var events = Map("""
            {"sessionUpdate":"tool_call","toolCallId":"tc1","title":"Viewing a.cs","kind":"read","status":"pending"}
            """);
        Assert.IsType<ToolCallEvent>(Assert.Single(events));
    }

    [Fact]
    public void Background_dispatch_is_noted()
    {
        var launch = AcpSubagentLaunch.TryParse(Json(CopilotTask).GetProperty("rawInput"))!;
        Assert.True(launch.IsBackground);
    }
}
