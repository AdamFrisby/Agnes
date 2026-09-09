using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Hosting;
using Agnes.Host.Mcp;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Agnes.Sandbox;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Agnes's own MCP server (<c>send_user_file</c>, <c>arm_goal</c>, …) has to actually appear in the config
/// each CLI reads, or the tools exist on the host and are reachable by nobody. These pin the three things
/// that silently produce "the agent has no Agnes tools": the entry missing from a format, the token landing
/// somewhere that CLI never reads, and the operator's own servers being dropped by the merge.
/// </summary>
public sealed class AgnesMcpMaterializationTests : IDisposable
{
    private const string GuestUrl = "http://10.99.5.1:5099/mcp-agnes";
    private const string LocalUrl = "http://127.0.0.1:5117/mcp-agnes";

    private readonly string _dir = Directory.CreateTempSubdirectory("agnes-mcp-mat").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp dir that outlives the run is noise, not a failure.
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    /// <summary>The minimum sandbox the MCP materialization touches: a home directory and a record of what
    /// was pushed in.</summary>
    private sealed class RecordingSandbox : ISandbox
    {
        public string Id => "fake-vm";
        public string HomeDirectory => "/home/agnes";
        public SandboxInfo Info => new("fake", Id, SandboxState.Running);
        public List<SandboxCredential> Materialised { get; } = [];

        public (string Command, IReadOnlyList<string> Arguments) WrapCommand(
            string command, IReadOnlyList<string> arguments, string workingDirectory)
            => (command, arguments);

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public Task MaterializeCredentialAsync(SandboxCredential credential, CancellationToken cancellationToken = default)
        {
            Materialised.Add(credential);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private SessionManager Manager(McpRegistry? registry = null, bool localEnabled = true, params string[] adapterIds)
        => new(
            TestPluginRegistries.Agents(adapterIds.Select(id => (IAgentAdapter)new ScriptedAgentAdapter(id)).ToArray()),
            new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            mcp: registry,
            guestMcp: new GuestMcpOptions { Url = GuestUrl, BindUrl = "http://10.99.5.1:5099" },
            localMcp: new LocalMcpOptions { Url = localEnabled ? LocalUrl : null, BindUrl = "http://127.0.0.1:5117" });

    private McpRegistry RegistryWith(params McpServerRequest[] servers)
    {
        var registry = new McpRegistry(Path.Combine(_dir, $"mcp-{Guid.NewGuid():n}.json"));
        foreach (var s in servers)
        {
            registry.Add(s);
        }

        return registry;
    }

    private static McpServerRequest SandboxStdio(string name) =>
        new(name, "sandbox", true, "stdio", Command: "npx", Args: ["-y", "mcp-" + name]);

    private static McpServerRequest HostStdio(string name) =>
        new(name, "host", true, "stdio", Command: "npx", Args: ["-y", "mcp-" + name]);

    /// <summary>Runs the sandboxed materialization for one adapter and returns the file it wrote.</summary>
    private (string? Path, string Content, IReadOnlyDictionary<string, string> Env) Sandboxed(
        SessionManager manager, string adapterId, string homeRelative)
    {
        var env = new Dictionary<string, string>();
        var files = new List<SandboxCredentialFile>();
        var path = manager.AddSandboxMcp(
            adapterId, new RecordingSandbox(), "sess-1", skipPermissions: false, mcpApproval: "Ask",
            project: null, workspaceId: null, env, files);

        var file = files.SingleOrDefault(f => f.HomeRelativePath == homeRelative);
        return (path, file?.Contents ?? string.Empty, env);
    }

    private static JsonElement AgnesEntry(string json)
        => JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("agnes");

    // ---- sandboxed: every config-file adapter, not just the model-environment one ----------------

    [Theory]
    [InlineData("claude-code-native")]
    [InlineData("copilot")]
    public void A_sandboxed_claude_shaped_agent_is_offered_the_agnes_server_on_the_guest_url(string adapterId)
    {
        var manager = Manager(adapterIds: adapterId);

        var (path, content, _) = Sandboxed(manager, adapterId, ".agnes/mcp.json");

        var agnes = AgnesEntry(content);
        Assert.Equal("http", agnes.GetProperty("type").GetString());
        Assert.Equal(GuestUrl, agnes.GetProperty("url").GetString());
        Assert.StartsWith("Bearer ", agnes.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("/home/agnes/.agnes/mcp.json", path);
    }

    [Fact]
    public void A_sandboxed_codex_gets_the_agnes_server_with_its_token_in_the_environment()
    {
        // Codex's config.toml has no header map: the bearer can only be named, and the launcher has to set
        // the variable. Rendering the entry without the variable would be an entry that authenticates as
        // nobody.
        var manager = Manager(adapterIds: "codex");

        var (path, content, env) = Sandboxed(manager, "codex", ".codex/config.toml");

        Assert.Contains("[mcp_servers.agnes]", content);
        Assert.Contains($"url = \"{GuestUrl}\"", content);
        Assert.Contains("bearer_token_env_var = \"AGNES_MCP_BEARER\"", content);
        Assert.True(env.TryGetValue("AGNES_MCP_BEARER", out var token) && token.Length > 0);
        Assert.DoesNotContain(token, content, StringComparison.Ordinal); // the token itself never lands in the file
        Assert.Null(path); // codex discovers its own config; there is no flag to pass
    }

    [Fact]
    public void An_adapter_with_no_mcp_client_is_written_nothing()
    {
        // Pi ships no MCP client and Antigravity exposes no MCP config surface. The table says so, and
        // nothing is materialized — an agnes entry in a file no CLI reads would only look like it worked.
        var manager = Manager(adapterIds: "pi");

        var env = new Dictionary<string, string>();
        var files = new List<SandboxCredentialFile>();
        var path = manager.AddSandboxMcp(
            "pi", new RecordingSandbox(), "sess-1", skipPermissions: false, mcpApproval: "Ask",
            project: null, workspaceId: null, env, files);

        Assert.Null(path);
        Assert.Empty(files);
        Assert.Empty(env);
    }

    // ---- unsandboxed: the loopback endpoint ------------------------------------------------------

    [Theory]
    [InlineData("claude-code-native")]
    [InlineData("copilot")]
    public async Task An_unsandboxed_agent_is_offered_the_agnes_server_on_loopback(string adapterId)
    {
        var manager = Manager(adapterIds: adapterId);

        var path = await manager.MaterializeHostMcpAsync(adapterId, $"host-{adapterId}", project: null, workspaceId: null, default);

        Assert.NotNull(path);
        var agnes = AgnesEntry(await File.ReadAllTextAsync(path!));
        Assert.Equal(LocalUrl, agnes.GetProperty("url").GetString());
        Assert.StartsWith("Bearer ", agnes.GetProperty("headers").GetProperty("Authorization").GetString());
        File.Delete(path!);
    }

    [Fact]
    public async Task With_the_loopback_listener_disabled_nothing_is_offered_locally()
    {
        var manager = Manager(localEnabled: false, adapterIds: "claude-code-native");

        Assert.Null(await manager.MaterializeHostMcpAsync("claude-code-native", Guid.NewGuid().ToString("n"), null, null, default));
    }

    [Fact]
    public async Task A_host_session_config_is_not_world_readable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var manager = Manager(adapterIds: "claude-code-native");
        var path = await manager.MaterializeHostMcpAsync("claude-code-native", Guid.NewGuid().ToString("n"), null, null, default);

        // It holds a session bearer; the default temp umask is not enough on a shared machine.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path!));
        File.Delete(path!);
    }

    [Fact]
    public async Task A_codex_host_session_gets_no_agnes_entry_because_agnes_does_not_own_that_config()
    {
        // ~/.codex/config.toml belongs to the person using this machine. Writing there to inject a server
        // would edit their configuration behind their back, so host-Codex is documented as unsupported
        // rather than quietly clobbered.
        var manager = Manager(adapterIds: "codex");

        Assert.Null(await manager.MaterializeHostMcpAsync("codex", Guid.NewGuid().ToString("n"), null, null, default));
    }

    // ---- the merge with the operator's own servers ------------------------------------------------

    [Fact]
    public void The_operators_own_sandbox_servers_survive_the_merge()
    {
        var manager = Manager(RegistryWith(SandboxStdio("files"), SandboxStdio("db")), adapterIds: "claude-code-native");

        var (_, content, _) = Sandboxed(manager, "claude-code-native", ".agnes/mcp.json");

        var servers = JsonDocument.Parse(content).RootElement.GetProperty("mcpServers");
        Assert.Equal(["agnes", "db", "files"], servers.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_operators_own_host_servers_survive_the_merge()
    {
        var manager = Manager(RegistryWith(HostStdio("files")), adapterIds: "claude-code-native");

        var path = await manager.MaterializeHostMcpAsync("claude-code-native", Guid.NewGuid().ToString("n"), null, null, default);
        var servers = JsonDocument.Parse(await File.ReadAllTextAsync(path!)).RootElement.GetProperty("mcpServers");

        Assert.Equal(["agnes", "files"], servers.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        File.Delete(path!);
    }

    [Fact]
    public void An_operator_defined_server_called_agnes_wins_and_ours_is_not_written()
    {
        // Silently replacing a server someone deliberately configured is worse than losing our own tools:
        // it would break their setup in a way nothing in the UI explains.
        var manager = Manager(
            RegistryWith(new McpServerRequest("agnes", "sandbox", true, "stdio", Command: "my-own-agnes")),
            adapterIds: "claude-code-native");

        var (_, content, _) = Sandboxed(manager, "claude-code-native", ".agnes/mcp.json");

        var agnes = AgnesEntry(content);
        Assert.Equal("my-own-agnes", agnes.GetProperty("command").GetString());
        Assert.False(agnes.TryGetProperty("headers", out _));
    }

    // ---- revocation ------------------------------------------------------------------------------

    [Fact]
    public void The_session_token_written_into_a_config_stops_resolving_once_revoked()
    {
        // Same SessionMcpTokens instance the endpoint authenticates against, so a config that outlives its
        // session authenticates as nothing — on the loopback listener exactly as on the bridge.
        var tokens = new SessionMcpTokens();
        var token = tokens.Issue("sess-1");

        Assert.Equal("sess-1", tokens.SessionFor(token));
        tokens.Revoke("sess-1");
        Assert.Null(tokens.SessionFor(token));
    }
}
