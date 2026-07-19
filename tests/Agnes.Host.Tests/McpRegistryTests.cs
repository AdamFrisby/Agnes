using Agnes.Host.Hosting;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

public class McpRegistryTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    private McpRegistry New() => new(_file);

    private static McpServerRequest Stdio(string name, string runAt = "host", bool enabled = true)
        => new(name, runAt, enabled, "stdio", Command: "npx", Args: ["-y", "some-mcp"]);

    [Fact]
    public void Add_assigns_an_id_and_normalizes_fields()
    {
        var reg = New();
        var added = reg.Add(Stdio("files"));

        Assert.False(string.IsNullOrWhiteSpace(added.Id));
        Assert.Equal("files", added.Name);
        Assert.Equal("host", added.RunAt);
        Assert.Equal("stdio", added.Transport);
        Assert.Equal("npx", added.Command);
        Assert.Equal(["-y", "some-mcp"], added.Args);
        Assert.True(added.Enabled);
    }

    [Fact]
    public void List_is_returned_and_remove_works()
    {
        var reg = New();
        var a = reg.Add(Stdio("alpha"));
        reg.Add(Stdio("bravo"));

        Assert.Equal(2, reg.List().Count);
        Assert.True(reg.Remove(a.Id));
        Assert.Single(reg.List());
        Assert.False(reg.Remove(a.Id)); // already gone
    }

    [Fact]
    public void Update_replaces_fields_and_toggles_enabled()
    {
        var reg = New();
        var s = reg.Add(Stdio("files"));

        var updated = reg.Update(s.Id, new McpServerRequest(
            "files", "sandbox", false, "stdio", Command: "uvx", Args: ["mcp-files"]));

        Assert.NotNull(updated);
        Assert.Equal("sandbox", updated!.RunAt);
        Assert.False(updated.Enabled);
        Assert.Equal("uvx", updated.Command);
        Assert.Null(reg.Update("no-such-id", Stdio("x"))); // unknown id
    }

    [Fact]
    public void Applicable_filters_by_run_location_and_enabled()
    {
        var reg = New();
        reg.Add(Stdio("host-on", "host", enabled: true));
        reg.Add(Stdio("host-off", "host", enabled: false));
        reg.Add(Stdio("sandbox-on", "sandbox", enabled: true));

        var host = reg.Applicable(McpRunAt.Host);
        Assert.Single(host);
        Assert.Equal("host-on", host[0].Name);

        var sandbox = reg.Applicable(McpRunAt.Sandbox);
        Assert.Single(sandbox);
        Assert.Equal("sandbox-on", sandbox[0].Name);
    }

    [Fact]
    public void Entries_persist_across_reloads()
    {
        var first = New();
        first.Add(Stdio("files"));
        first.Add(new McpServerRequest("remote", "host", true, "http", Url: "https://mcp.example/mcp"));

        var reloaded = New();
        var list = reloaded.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Name == "remote" && s.Transport == "http" && s.Url == "https://mcp.example/mcp");
    }
}
