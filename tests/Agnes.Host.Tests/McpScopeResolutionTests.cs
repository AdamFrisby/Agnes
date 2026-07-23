using System.Text.Json;
using Agnes.Host.Hosting;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

/// <summary>
/// The MCP-management scope/strict/preset surface (.ideas/extensibility/01-mcp-management.md): scope
/// filtering for the effective set, preset quick-install via the normal add path, strict-vs-lenient
/// startup resolution, and back-compat for entries persisted before scopes existed.
/// </summary>
public class McpScopeResolutionTests : IDisposable
{
    // A temp path (no absolute-path literals — PH2080) that each test's registry persists to.
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"agnes-mcp-scope-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    private McpRegistry New() => new(_file);

    private static McpServerRequest Stdio(
        string name, McpApplyScope scope = McpApplyScope.AllHosts, string? workspaceId = null, string runAt = "host")
        => new(name, runAt, Enabled: true, "stdio", Command: "npx", Args: ["-y", "some-mcp"],
            ApplyScope: scope, WorkspaceId: workspaceId);

    [Fact]
    public void EffectiveFor_includes_all_hosts_and_excludes_a_different_workspace()
    {
        var reg = New();
        reg.Add(Stdio("global", McpApplyScope.AllHosts));
        reg.Add(Stdio("ws-a-only", McpApplyScope.ThisWorkspace, workspaceId: "ws-a"));

        var forB = reg.EffectiveFor("ws-b");
        Assert.Contains(forB, s => s.Name == "global");           // AllHosts always applies
        Assert.DoesNotContain(forB, s => s.Name == "ws-a-only");  // scoped to a different workspace

        var forA = reg.EffectiveFor("ws-a");
        Assert.Contains(forA, s => s.Name == "global");
        Assert.Contains(forA, s => s.Name == "ws-a-only");        // matches its own workspace

        // An unknown/absent workspace never matches a ThisWorkspace entry, but still sees AllHosts.
        var forNone = reg.EffectiveFor(null);
        Assert.Contains(forNone, s => s.Name == "global");
        Assert.DoesNotContain(forNone, s => s.Name == "ws-a-only");
    }

    [Fact]
    public void ThisHost_scope_applies_on_this_host_regardless_of_workspace()
    {
        var reg = New();
        reg.Add(Stdio("host-wide", McpApplyScope.ThisHost));

        Assert.Contains(reg.EffectiveFor("ws-a"), s => s.Name == "host-wide");
        Assert.Contains(reg.EffectiveFor(null), s => s.Name == "host-wide");
    }

    [Fact]
    public void Installing_a_preset_persists_a_matching_server()
    {
        // A preset template (as the curated provider ships them) -> the normal add-server request path.
        var template = new CuratedMcpPresetProvider().Presets.First(p => p.Id == "playwright");

        var reg = New();
        var installed = reg.Add(new McpServerRequest(
            template.Name, template.RunAt, template.Enabled, template.Transport,
            template.Command, template.Args, template.Env, template.Url, template.BearerTokenEnv));

        Assert.False(string.IsNullOrWhiteSpace(installed.Id));
        Assert.Equal(template.Name, installed.Name);
        Assert.Equal(template.Command, installed.Command);
        Assert.Equal(template.Args, installed.Args);

        // Persisted: a fresh registry over the same file sees it.
        Assert.Contains(New().List(), s => s.Name == template.Name && s.Command == template.Command);
    }

    [Fact]
    public void Lenient_resolution_skips_an_unresolvable_server_with_a_warning()
    {
        var reg = New();
        reg.Add(Stdio("good"));
        // Enabled stdio server with no command => can't be resolved.
        reg.Add(new McpServerRequest("broken", "host", Enabled: true, "stdio", Command: null));

        var resolution = reg.Resolve(McpRunAt.Host, workspaceId: null, strict: false);

        Assert.Contains(resolution.Servers, s => s.Name == "good");
        Assert.DoesNotContain(resolution.Servers, s => s.Name == "broken");
        Assert.Contains(resolution.Warnings, w => w.Contains("broken", StringComparison.Ordinal));
    }

    [Fact]
    public void Strict_resolution_throws_naming_the_unresolvable_server()
    {
        var reg = New();
        reg.Add(Stdio("good"));
        reg.Add(new McpServerRequest("broken", "host", Enabled: true, "stdio", Command: null));

        var ex = Assert.Throws<McpResolutionException>(() => reg.Resolve(McpRunAt.Host, workspaceId: null, strict: true));
        Assert.Contains("broken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void McpServerInfo_without_apply_scope_deserializes_to_all_hosts()
    {
        // A wire payload persisted before ApplyScope existed (the field is simply absent).
        const string legacyJson = """
        {
          "id": "abc",
          "name": "files",
          "runAt": "host",
          "enabled": true,
          "transport": "stdio",
          "command": "npx",
          "args": ["-y", "some-mcp"],
          "env": {},
          "url": null,
          "bearerTokenEnv": null
        }
        """;

        var info = JsonSerializer.Deserialize<McpServerInfo>(legacyJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(info);
        Assert.Equal(McpApplyScope.AllHosts, info!.ApplyScope);
        Assert.Null(info.WorkspaceId);
    }
}
