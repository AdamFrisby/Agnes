using System.Text.Json;
using Agnes.Cli;

namespace Agnes.Cli.Tests;

public sealed class JsonOutputTests
{
    [Fact]
    public void Status_json_is_valid_and_has_the_expected_fields()
    {
        var json = JsonOutput.Render(new StatusJson("sim-0001", "claude-code", Path.GetTempPath(), "idle", 7));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("sim-0001", root.GetProperty("sessionId").GetString());
        Assert.Equal("claude-code", root.GetProperty("adapter").GetString());
        Assert.Equal("idle", root.GetProperty("state").GetString());
        Assert.Equal(7, root.GetProperty("headSequence").GetInt64());
    }

    [Fact]
    public void Machines_json_is_a_valid_array_of_objects()
    {
        var json = JsonOutput.Render(new List<MachineJson>
        {
            new("laptop", "https://host:5081", Reachable: true, "My Host", "0.1.0"),
            new("offline", "https://other:5081", Reachable: false, null, null),
        });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var first = doc.RootElement[0];
        Assert.Equal("laptop", first.GetProperty("id").GetString());
        Assert.True(first.GetProperty("reachable").GetBoolean());
        Assert.Equal("My Host", first.GetProperty("displayName").GetString());

        // A null displayName is omitted (WhenWritingNull) rather than emitted as null.
        var second = doc.RootElement[1];
        Assert.False(second.GetProperty("reachable").GetBoolean());
        Assert.False(second.TryGetProperty("displayName", out _));
    }

    [Fact]
    public void Spawn_json_carries_the_session_id()
    {
        var json = JsonOutput.Render(new SpawnJson("sim-0001", "claude-code", "laptop"));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("sim-0001", doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("claude-code", doc.RootElement.GetProperty("adapter").GetString());
    }
}
