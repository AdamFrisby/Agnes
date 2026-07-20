using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Sandbox;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class SandboxWiringTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    /// <summary>A fake sandbox that records lifecycle calls and wraps commands like Incus would.</summary>
    private sealed class FakeSandbox : ISandbox, IPausableSandbox
    {
        public string Id { get; } = "fake-vm-1";
        public string HomeDirectory => "/home/agnes";
        public bool IsPaused { get; private set; }
        public bool Deleted { get; private set; }
        public List<SandboxCredential> Materialised { get; } = [];
        public (string Command, IReadOnlyList<string> Args, string Cwd)? LastWrap { get; private set; }

        public SandboxInfo Info => new("fake", Id, IsPaused ? SandboxState.Paused : (Deleted ? SandboxState.Stopped : SandboxState.Running));

        public (string Command, IReadOnlyList<string> Arguments) WrapCommand(
            string command, IReadOnlyList<string> arguments, string workingDirectory)
        {
            LastWrap = (command, arguments, workingDirectory);
            var argv = new List<string> { "exec", Id, "--", command };
            argv.AddRange(arguments);
            return ("fakebox", argv);
        }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task MaterializeCredentialAsync(SandboxCredential credential, CancellationToken cancellationToken = default)
        {
            Materialised.Add(credential);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default) { IsPaused = true; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken = default) { IsPaused = false; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken cancellationToken = default) { Deleted = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // persist — never deletes
    }

    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        public FakeSandbox Last { get; private set; } = null!;
        public List<SandboxSpec> Specs { get; } = [];
        public string Name => "fake";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
        {
            Specs.Add(spec);
            Last = new FakeSandbox();
            return Task.FromResult<ISandbox>(Last);
        }

        public Task<IReadOnlyList<SandboxInfo>> ListManagedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInfo>>([]);
    }

    private sealed class FakeCredentialProvider : IAgentCredentialProvider
    {
        public bool Handles(string adapterId) => adapterId == "scripted";

        public Task<SandboxCredential> GetAsync(string adapterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxCredential
            {
                EnvironmentVariables = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" },
                Files = [new SandboxCredentialFile(".claude/.credentials.json", "{}")],
            });
    }

    [Fact]
    public async Task Open_session_provisions_sandbox_materialises_credentials_and_wraps_launch()
    {
        var adapter = new ScriptedAgentAdapter();
        var sandboxes = new FakeSandboxProvider();
        var credentials = new FakeCredentialProvider();
        await using var manager = new SessionManager(
            [adapter], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            sandboxes, [credentials]);

        var info = await manager.OpenSessionAsync("scripted", "/home/adam/project");

        // A sandbox was provisioned for the host working directory.
        var spec = Assert.Single(sandboxes.Specs);
        Assert.Equal("/home/adam/project", spec.HostWorkingDirectory);

        // Credentials were materialised into it.
        Assert.Single(sandboxes.Last.Materialised);

        // The agent was launched INSIDE the sandbox (options carried the wrap seam, cwd = /work).
        Assert.NotNull(adapter.LastOptions);
        Assert.Same(sandboxes.Last, adapter.LastOptions!.Sandbox);
        Assert.Equal("/work", adapter.LastOptions.WorkingDirectory);

        // The returned session info reports the sandbox.
        Assert.NotNull(info.Sandbox);
        Assert.Equal("fake", info.Sandbox!.Provider);
        Assert.Equal("Running", info.Sandbox.State);
    }

    [Fact]
    public async Task Sandbox_lifecycle_routes_pause_resume_delete()
    {
        var adapter = new ScriptedAgentAdapter();
        var sandboxes = new FakeSandboxProvider();
        await using var manager = new SessionManager(
            [adapter], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            sandboxes, [new FakeCredentialProvider()]);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work");
        var sandbox = sandboxes.Last;

        await manager.PauseSandboxAsync(info.SessionId);
        Assert.True(sandbox.IsPaused);
        Assert.Equal("Paused", manager.GetSandboxStatus(info.SessionId)!.State);

        await manager.ResumeSandboxAsync(info.SessionId);
        Assert.False(sandbox.IsPaused);
        Assert.Equal("Running", manager.GetSandboxStatus(info.SessionId)!.State);

        await manager.DeleteSandboxAsync(info.SessionId);
        Assert.True(sandbox.Deleted);
        Assert.Null(manager.GetSandboxStatus(info.SessionId));
    }

    [Fact]
    public async Task Sandbox_session_uses_its_projects_mcp_servers()
    {
        var projectsFile = Path.Combine(Path.GetTempPath(), $"agnes-proj-{Guid.NewGuid():n}.json");
        try
        {
            var projects = new Agnes.Host.Projects.ProjectStore(projectsFile);
            projects.Save(projects.Default() with
            {
                McpServers =
                [
                    new Agnes.Protocol.McpServerInfo("id1", "proj-files", "sandbox", true, "stdio", "npx", ["-y", "mcp"], new Dictionary<string, string>(), null, null),
                ],
            });

            var adapter = new ScriptedAgentAdapter("codex");
            var sandboxes = new FakeSandboxProvider();
            await using var manager = new SessionManager(
                [adapter], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                sandboxes, [new FakeCredentialProvider()], projects: projects);

            await manager.OpenSessionAsync("codex", "/tmp/project"); // no repo → the default project

            var configFile = sandboxes.Last.Materialised.SelectMany(c => c.Files)
                .FirstOrDefault(f => f.HomeRelativePath == ".codex/config.toml");
            Assert.NotNull(configFile);
            Assert.Contains("proj-files", configFile!.Contents); // the project's MCP server (no global registry present)
        }
        finally
        {
            if (File.Exists(projectsFile)) File.Delete(projectsFile);
        }
    }

    [Fact]
    public async Task Sandbox_session_materialises_mcp_config_for_run_at_sandbox_servers()
    {
        var mcpFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");
        try
        {
            var mcp = new Agnes.Host.Hosting.McpRegistry(mcpFile);
            mcp.Add(new Agnes.Protocol.McpServerRequest("files", "sandbox", true, "stdio", Command: "npx", Args: ["-y", "mcp"]));
            mcp.Add(new Agnes.Protocol.McpServerRequest("host-only", "host", true, "stdio", Command: "x")); // excluded

            var adapter = new ScriptedAgentAdapter("codex");
            var sandboxes = new FakeSandboxProvider();
            await using var manager = new SessionManager(
                [adapter], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                sandboxes, [new FakeCredentialProvider()], null, mcp);

            await manager.OpenSessionAsync("codex", "/tmp/project");

            var configFile = sandboxes.Last.Materialised
                .SelectMany(c => c.Files)
                .FirstOrDefault(f => f.HomeRelativePath == ".codex/config.toml");
            Assert.NotNull(configFile);
            Assert.Contains("[mcp_servers.files]", configFile!.Contents);
            Assert.DoesNotContain("host-only", configFile.Contents); // RunAt=host excluded from a sandbox session
        }
        finally
        {
            if (File.Exists(mcpFile)) File.Delete(mcpFile);
        }
    }

    [Fact]
    public async Task Sandbox_session_forwards_run_at_host_servers_via_the_shim()
    {
        var mcpFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");
        try
        {
            var mcp = new Agnes.Host.Hosting.McpRegistry(mcpFile);
            mcp.Add(new Agnes.Protocol.McpServerRequest("host-tool", "host", true, "stdio", Command: "real-mcp"));

            var forward = new Agnes.Host.Hosting.McpForwardRegistry();
            await using var listener = new Agnes.Host.Hosting.McpForwardListener(
                forward, System.Net.IPAddress.Loopback, 0, "127.0.0.1",
                NullLogger<Agnes.Host.Hosting.McpForwardListener>.Instance);
            listener.Start();

            var adapter = new ScriptedAgentAdapter("codex");
            var sandboxes = new FakeSandboxProvider();
            await using var manager = new SessionManager(
                [adapter], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                sandboxes, [new FakeCredentialProvider()], null, mcp, forward, listener);

            await manager.OpenSessionAsync("codex", "/tmp/project");

            var bundle = Assert.Single(sandboxes.Last.Materialised);

            // The forward shim was materialized, and the config launches it (not the real command).
            Assert.Contains(bundle.Files, f => f.HomeRelativePath == ".agnes/mcp-forward.py");
            var config = bundle.Files.First(f => f.HomeRelativePath == ".codex/config.toml").Contents;
            Assert.Contains("mcp-forward.py", config);
            Assert.DoesNotContain("real-mcp", config); // the real host command never enters the VM

            // The shim's connection details ride the (single) env bundle.
            Assert.Equal("127.0.0.1", bundle.EnvironmentVariables["AGNES_MCP_HOST"]);
            Assert.Equal(listener.Port.ToString(), bundle.EnvironmentVariables["AGNES_MCP_PORT"]);
            Assert.True(bundle.EnvironmentVariables.ContainsKey("AGNES_MCP_TOKEN"));

            // And the token actually resolves the granted server in the forward registry.
            var token = bundle.EnvironmentVariables["AGNES_MCP_TOKEN"];
            Assert.NotNull(forward.Resolve(token, "host-tool"));
        }
        finally
        {
            if (File.Exists(mcpFile)) File.Delete(mcpFile);
        }
    }

    [Theory]
    [InlineData(true, "Ask", false)]   // autonomous + Ask → host servers withheld
    [InlineData(true, "Trust", true)]  // autonomous + Trust → forwarded
    [InlineData(false, "Ask", true)]   // attended → always forwarded (agent prompts per tool)
    public async Task Autonomous_forwarding_respects_the_approval_preference(bool autonomous, string approval, bool expectForwarded)
    {
        var mcpFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");
        try
        {
            var mcp = new Agnes.Host.Hosting.McpRegistry(mcpFile);
            mcp.Add(new Agnes.Protocol.McpServerRequest("host-tool", "host", true, "stdio", Command: "real-mcp"));

            var forward = new Agnes.Host.Hosting.McpForwardRegistry();
            await using var listener = new Agnes.Host.Hosting.McpForwardListener(
                forward, System.Net.IPAddress.Loopback, 0, "127.0.0.1",
                NullLogger<Agnes.Host.Hosting.McpForwardListener>.Instance);
            listener.Start();

            var sandboxes = new FakeSandboxProvider();
            await using var manager = new SessionManager(
                [new ScriptedAgentAdapter("codex")], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                sandboxes, [new FakeCredentialProvider()], null, mcp, forward, listener);

            await manager.OpenSessionAsync("codex", "/tmp/project", skipPermissions: autonomous, mcpApproval: approval);

            var forwarded = sandboxes.Last.Materialised
                .SelectMany(c => c.Files)
                .Any(f => f.HomeRelativePath == ".agnes/mcp-forward.py");
            Assert.Equal(expectForwarded, forwarded);
        }
        finally
        {
            if (File.Exists(mcpFile)) File.Delete(mcpFile);
        }
    }

    [Fact]
    public async Task Host_claude_native_session_gets_an_mcp_config_path_flag()
    {
        var mcpFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");
        try
        {
            var mcp = new Agnes.Host.Hosting.McpRegistry(mcpFile);
            mcp.Add(new Agnes.Protocol.McpServerRequest("files", "host", true, "stdio", Command: "npx", Args: ["-y", "mcp"]));

            var adapter = new ScriptedAgentAdapter("claude-code-native");
            await using var manager = new SessionManager(
                [adapter], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance, mcp: mcp);

            await manager.OpenSessionAsync("claude-code-native", "/tmp/work");

            var path = adapter.LastOptions!.McpConfigPath;
            Assert.False(string.IsNullOrEmpty(path));
            Assert.Contains("mcpServers", await File.ReadAllTextAsync(path!));
            File.Delete(path!);
        }
        finally
        {
            if (File.Exists(mcpFile)) File.Delete(mcpFile);
        }
    }

    [Fact]
    public void Sandboxed_agent_availability_reflects_the_baked_image()
    {
        var file = Path.Combine(Path.GetTempPath(), $"agnes-img-{Guid.NewGuid():n}.json");
        try
        {
            // Default manifest bakes claude-code-native + codex, not opencode.
            var images = new Agnes.Host.Sessions.SandboxImageManager(
                new SandboxImageManagerTests.FakeImageBuilder { Exists = true }, file,
                NullLogger<Agnes.Host.Sessions.SandboxImageManager>.Instance);
            var manager = new SessionManager(
                [new ScriptedAgentAdapter("codex"), new ScriptedAgentAdapter("opencode")],
                new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance, images: images);

            var agents = manager.ListAgents();
            Assert.True(agents.Single(a => a.AdapterId == "codex").Available);
            Assert.False(agents.Single(a => a.AdapterId == "opencode").Available);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Sandbox_open_bakes_a_missing_image_and_launches_from_the_alias()
    {
        var file = Path.Combine(Path.GetTempPath(), $"agnes-img-{Guid.NewGuid():n}.json");
        try
        {
            var builder = new SandboxImageManagerTests.FakeImageBuilder { Exists = false };
            var images = new Agnes.Host.Sessions.SandboxImageManager(
                builder, file, NullLogger<Agnes.Host.Sessions.SandboxImageManager>.Instance);
            var sandboxes = new FakeSandboxProvider();
            await using var manager = new SessionManager(
                [new ScriptedAgentAdapter("codex")], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                sandboxes, [new FakeCredentialProvider()], images: images);

            await manager.OpenSessionAsync("codex", "/tmp/project");

            Assert.Equal(1, builder.Builds); // baked once because it was missing
            Assert.Equal("agnes-baseline", sandboxes.Specs.Single().ImageReference); // launched from the baked alias
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task No_sandbox_provider_leaves_agent_on_host()
    {
        var adapter = new ScriptedAgentAdapter();
        await using var manager = new SessionManager(
            [adapter], new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work");

        Assert.Null(info.Sandbox);
        Assert.NotNull(adapter.LastOptions);
        Assert.Null(adapter.LastOptions!.Sandbox);
        Assert.Equal("/tmp/work", adapter.LastOptions.WorkingDirectory);
    }
}
