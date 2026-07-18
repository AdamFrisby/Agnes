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
}
