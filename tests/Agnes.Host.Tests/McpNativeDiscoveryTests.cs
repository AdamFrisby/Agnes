using Agnes.Abstractions;
using Agnes.Agents.ClaudeCode;
using Agnes.Host.Hosting;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

/// <summary>
/// Native-config detection for MCP (.ideas/extensibility/01-mcp-management.md): an agent CLI's own
/// natively-configured MCP servers surfaced read-only and folded into the effective-config preview
/// (<see cref="McpEffectiveConfig"/>), plus the Claude Code detector that reads its config files.
/// </summary>
public class McpNativeDiscoveryTests : IDisposable
{
    // Temp paths only (no absolute-path literals — PH2080).
    private readonly string _regFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-native-{Guid.NewGuid():n}.json");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"agnes-ws-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (File.Exists(_regFile)) File.Delete(_regFile);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    private static NativeMcpServer Native(string name, string command = "npx")
        => new(name, "stdio", command, ["-y", $"{name}-mcp"], new Dictionary<string, string>(), null, "Fake CLI native config");

    private static McpServerRequest Managed(string name)
        => new(name, "host", Enabled: true, "stdio", Command: "npx", Args: ["-y", "managed-mcp"]);

    [Fact]
    public async Task Preview_unions_native_servers_with_managed_ones_flagged_read_only()
    {
        var reg = new McpRegistry(_regFile);
        reg.Add(Managed("agnes-server"));

        var adapters = new IAgentAdapter[]
        {
            new FakeDiscoveryAgent("fake", [Native("browser"), Native("docs")]),
        };

        var effective = await McpEffectiveConfig.PreviewAsync(reg, adapters, _workspace);

        // The Agnes-managed one is present and NOT flagged native.
        var managed = Assert.Single(effective, s => s.Name == "agnes-server");
        Assert.False(managed.NativeConfig);

        // Both native ones are present, flagged native/read-only with a source label.
        foreach (var name in new[] { "browser", "docs" })
        {
            var native = Assert.Single(effective, s => s.Name == name);
            Assert.True(native.NativeConfig);
            Assert.Equal("Fake CLI native config", native.Source);
        }
    }

    [Fact]
    public async Task Adapter_without_the_capability_surfaces_no_native_servers()
    {
        var reg = new McpRegistry(_regFile);
        reg.Add(Managed("agnes-server"));

        // A plain adapter that does NOT implement IMcpDiscoveryAdapter — capability absent, graceful.
        var adapters = new IAgentAdapter[] { new FakeAgent("plain") };

        var effective = await McpEffectiveConfig.PreviewAsync(reg, adapters, _workspace);

        Assert.DoesNotContain(effective, s => s.NativeConfig);
        Assert.Single(effective, s => s.Name == "agnes-server");
    }

    [Fact]
    public async Task Name_collision_is_deduped_with_the_native_one_flagged()
    {
        var reg = new McpRegistry(_regFile);
        reg.Add(Managed("shared"));

        var adapters = new IAgentAdapter[] { new FakeDiscoveryAgent("fake", [Native("shared")]) };

        var effective = await McpEffectiveConfig.PreviewAsync(reg, adapters, _workspace);

        // Exactly one "shared" entry (no double-list), and it's the native/read-only one.
        var shared = Assert.Single(effective, s => s.Name == "shared");
        Assert.True(shared.NativeConfig);
    }

    [Fact]
    public async Task No_workspace_directory_means_no_native_detection()
    {
        var reg = new McpRegistry(_regFile);
        reg.Add(Managed("agnes-server"));
        var adapters = new IAgentAdapter[] { new FakeDiscoveryAgent("fake", [Native("browser")]) };

        var effective = await McpEffectiveConfig.PreviewAsync(reg, adapters, workspaceId: null);

        Assert.DoesNotContain(effective, s => s.NativeConfig);
    }

    [Fact]
    public async Task ClaudeCode_detector_parses_a_project_mcp_json()
    {
        Directory.CreateDirectory(_workspace);
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".mcp.json"), """
        {
          "mcpServers": {
            "playwright": { "command": "npx", "args": ["-y", "@playwright/mcp@latest"], "env": { "K": "V" } },
            "remote": { "type": "http", "url": "https://example.test/mcp" }
          }
        }
        """);

        var servers = await ClaudeNativeMcpConfig.DetectAsync(_workspace);

        var playwright = Assert.Single(servers, s => s.Name == "playwright");
        Assert.Equal("stdio", playwright.Transport);
        Assert.Equal("npx", playwright.Command);
        Assert.Equal(["-y", "@playwright/mcp@latest"], playwright.Args);
        Assert.Equal("V", playwright.Env["K"]);
        Assert.Equal(ClaudeNativeMcpConfig.SourceLabel, playwright.SourceLabel);

        var remote = Assert.Single(servers, s => s.Name == "remote");
        Assert.Equal("http", remote.Transport);
        Assert.Equal("https://example.test/mcp", remote.Url);
    }

    [Fact]
    public async Task ClaudeCode_detector_tolerates_missing_and_malformed_config()
    {
        // Parse the file directly (hermetic — doesn't also read the machine's real ~/.claude.json).
        var file = Path.Combine(_workspace, ".mcp.json");

        // Missing file => empty, no throw.
        Assert.Empty(ClaudeNativeMcpConfig.ParseFile(file, projectDirectory: null));

        // Malformed JSON => empty, no throw.
        Directory.CreateDirectory(_workspace);
        await File.WriteAllTextAsync(file, "{ not valid json ]");
        Assert.Empty(ClaudeNativeMcpConfig.ParseFile(file, projectDirectory: null));
    }

    [Fact]
    public void ClaudeCode_detector_reads_a_per_project_section_of_the_global_config()
    {
        // The ~/.claude.json shape: a top-level mcpServers plus per-project overrides keyed by directory.
        const string json = """
        {
          "mcpServers": { "global-one": { "command": "npx", "args": ["-y", "g"] } },
          "projects": {
            "/home/me/proj": { "mcpServers": { "proj-one": { "command": "node", "args": ["s.js"] } } }
          }
        }
        """;

        var servers = ClaudeNativeMcpConfig.ParseContent(json, projectDirectory: "/home/me/proj");

        Assert.Single(servers, s => s.Name == "global-one");
        Assert.Single(servers, s => s.Name == "proj-one" && s.Command == "node");
    }

    private sealed class FakeAgent(string id) : IAgentAdapter
    {
        public AgentDescriptor Descriptor { get; } = new() { Id = id, DisplayName = id };

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeDiscoveryAgent(string id, IReadOnlyList<NativeMcpServer> servers)
        : IAgentAdapter, IMcpDiscoveryAdapter
    {
        public AgentDescriptor Descriptor { get; } = new() { Id = id, DisplayName = id };

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<NativeMcpServer>> DetectNativeConfigAsync(string workspaceDirectory, CancellationToken ct = default)
            => Task.FromResult(servers);
    }
}
