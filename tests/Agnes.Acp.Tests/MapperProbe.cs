using System.Text.Json;
using Agnes.Abstractions;

namespace Agnes.Acp.Tests;

public class MapperProbe
{
    [Fact]
    public void ToEvents_maps_agent_message_chunk()
    {
        using var doc = JsonDocument.Parse(
            "{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Hi\"}}");
        var events = Agnes.Acp.AcpMap.ToEvents(doc.RootElement).ToList();
        Assert.True(events.Count == 1, "count=" + events.Count);
        var msg = Assert.IsType<MessageChunkEvent>(events[0]);
        Assert.Equal("Hi", ((TextContent)msg.Content).Text);
    }

    [Fact]
    public void ToEvents_maps_structured_diff_tool_content()
    {
        using var doc = JsonDocument.Parse(
            "{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"tc1\",\"title\":\"Edit\",\"kind\":\"edit\",\"status\":\"completed\"," +
            "\"content\":[{\"type\":\"diff\",\"path\":\"src/a.ts\",\"oldText\":\"a\",\"newText\":\"b\"}]}");
        var events = Agnes.Acp.AcpMap.ToEvents(doc.RootElement).ToList();

        var tool = Assert.IsType<ToolCallEvent>(Assert.Single(events));
        var diff = Assert.IsType<DiffContent>(Assert.Single(tool.Content));
        Assert.Equal("src/a.ts", diff.Path);
        Assert.Equal("a", diff.OldText);
        Assert.Equal("b", diff.NewText);
    }

    // Golden mapping: every ACP tool `kind` string → the canonical ToolKind taxonomy; unknown/absent → Other.
    // Locks the convention so a future edit to the switch (or a new ToolKind) can't silently drift.
    [Theory]
    [InlineData("read", ToolKind.Read)]
    [InlineData("edit", ToolKind.Edit)]
    [InlineData("delete", ToolKind.Delete)]
    [InlineData("move", ToolKind.Move)]
    [InlineData("search", ToolKind.Search)]
    [InlineData("execute", ToolKind.Execute)]
    [InlineData("think", ToolKind.Think)]
    [InlineData("fetch", ToolKind.Fetch)]
    [InlineData("other", ToolKind.Other)]
    [InlineData("something-new", ToolKind.Other)]
    [InlineData(null, ToolKind.Other)]
    public void ToToolKind_maps_acp_kinds_to_the_canonical_taxonomy(string? kind, ToolKind expected)
        => Assert.Equal(expected, Agnes.Acp.AcpMap.ToToolKind(kind));
}
