using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
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

        public Task<ISandbox> AttachAsync(string vmName, SandboxSpec spec, bool start, CancellationToken cancellationToken = default)
        {
            Last = new FakeSandbox();
            return Task.FromResult<ISandbox>(Last);
        }
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
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(sandboxes), [credentials]);

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
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()]);

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
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()], projects: projects);

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
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()], null, mcp);

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
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()], null, mcp, forward, listener);

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
                TestPluginRegistries.Agents(new ScriptedAgentAdapter("codex")), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()], null, mcp, forward, listener);

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
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance, mcp: mcp);

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
                TestPluginRegistries.Agents(new ScriptedAgentAdapter("codex"), new ScriptedAgentAdapter("opencode")),
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
                TestPluginRegistries.Agents(new ScriptedAgentAdapter("codex")), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()], images: images);

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
    public async Task Require_sandbox_still_opens_a_sandboxed_session()
    {
        var adapter = new ScriptedAgentAdapter();
        var sandboxes = new FakeSandboxProvider();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()],
            security: new SessionSecurityOptions { RequireSandbox = true });

        var info = await manager.OpenSessionAsync("scripted", "/home/adam/project"); // useSandbox defaults true

        Assert.NotNull(info.Sandbox); // the require-sandbox guard let a genuinely sandboxed session through
        Assert.Same(sandboxes.Last, adapter.LastOptions!.Sandbox);
    }

    [Fact]
    public async Task Autonomous_is_allowed_inside_a_sandbox_by_default()
    {
        var adapter = new ScriptedAgentAdapter();
        var sandboxes = new FakeSandboxProvider();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()],
            security: new SessionSecurityOptions()); // unsandboxed autonomy disallowed by default — but this IS sandboxed

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", skipPermissions: true);

        Assert.NotNull(info.Sandbox);
        Assert.True(adapter.LastOptions!.SkipPermissions);
    }

    [Fact]
    public async Task Require_permission_prompts_refuses_even_a_sandboxed_autonomous_session()
    {
        var adapter = new ScriptedAgentAdapter();
        var sandboxes = new FakeSandboxProvider();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()],
            security: new SessionSecurityOptions { RequirePermissionPrompts = true });

        await Assert.ThrowsAsync<SessionSecurityException>(
            () => manager.OpenSessionAsync("scripted", "/tmp/work", skipPermissions: true));

        Assert.Null(adapter.LastOptions); // never launched
    }

    [Fact]
    public async Task Host_mcp_allowlist_drops_disallowed_servers_from_the_forward_path()
    {
        var mcpFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");
        try
        {
            var mcp = new Agnes.Host.Hosting.McpRegistry(mcpFile);
            mcp.Add(new Agnes.Protocol.McpServerRequest("allowed-tool", "host", true, "stdio", Command: "real-allowed"));
            mcp.Add(new Agnes.Protocol.McpServerRequest("blocked-tool", "host", true, "stdio", Command: "real-blocked"));

            var forward = new Agnes.Host.Hosting.McpForwardRegistry();
            await using var listener = new Agnes.Host.Hosting.McpForwardListener(
                forward, System.Net.IPAddress.Loopback, 0, "127.0.0.1",
                NullLogger<Agnes.Host.Hosting.McpForwardListener>.Instance);
            listener.Start();

            var sandboxes = new FakeSandboxProvider();
            await using var manager = new SessionManager(
                TestPluginRegistries.Agents(new ScriptedAgentAdapter("codex")), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(sandboxes), [new FakeCredentialProvider()], null, mcp, forward, listener,
                security: new SessionSecurityOptions { AllowedHostMcpServers = ["allowed-tool"] });

            await manager.OpenSessionAsync("codex", "/tmp/project");

            var bundle = Assert.Single(sandboxes.Last.Materialised);
            var config = bundle.Files.First(f => f.HomeRelativePath == ".codex/config.toml").Contents;
            Assert.Contains("allowed-tool", config);
            Assert.DoesNotContain("blocked-tool", config); // dropped by the host-MCP allowlist

            // The forward token grants ONLY the allowed server — the blocked one can never be spawned on the host.
            var token = bundle.EnvironmentVariables["AGNES_MCP_TOKEN"];
            Assert.NotNull(forward.Resolve(token, "allowed-tool"));
            Assert.Null(forward.Resolve(token, "blocked-tool"));
        }
        finally
        {
            if (File.Exists(mcpFile)) File.Delete(mcpFile);
        }
    }

    [Fact]
    public async Task Host_mcp_allowlist_drops_disallowed_servers_from_the_direct_config()
    {
        var mcpFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");
        try
        {
            var mcp = new Agnes.Host.Hosting.McpRegistry(mcpFile);
            mcp.Add(new Agnes.Protocol.McpServerRequest("allowed", "host", true, "stdio", Command: "npx", Args: ["-y", "a"]));
            mcp.Add(new Agnes.Protocol.McpServerRequest("blocked", "host", true, "stdio", Command: "npx", Args: ["-y", "b"]));

            var adapter = new ScriptedAgentAdapter("claude-code-native");
            await using var manager = new SessionManager(
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
                mcp: mcp, security: new SessionSecurityOptions { AllowedHostMcpServers = ["allowed"] });

            await manager.OpenSessionAsync("claude-code-native", "/tmp/work");

            var config = await File.ReadAllTextAsync(adapter.LastOptions!.McpConfigPath!);
            Assert.Contains("allowed", config);
            Assert.DoesNotContain("blocked", config); // dropped from the host-run agent's own MCP config
            File.Delete(adapter.LastOptions.McpConfigPath!);
        }
        finally
        {
            if (File.Exists(mcpFile)) File.Delete(mcpFile);
        }
    }

    [Fact]
    public async Task No_sandbox_provider_leaves_agent_on_host()
    {
        var adapter = new ScriptedAgentAdapter();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work");

        Assert.Null(info.Sandbox);
        Assert.NotNull(adapter.LastOptions);
        Assert.Null(adapter.LastOptions!.Sandbox);
        Assert.Equal("/tmp/work", adapter.LastOptions.WorkingDirectory);
    }

    [Fact]
    public async Task GetCapabilities_reports_sandbox_provider_unavailable_when_none_configured()
    {
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var capabilities = manager.GetCapabilities();

        var sandbox = Assert.Single(capabilities, c => c.Id == HostCapabilityIds.SandboxProvider);
        Assert.False(sandbox.Available);
        Assert.False(sandbox.FailClosed); // absence degrades gracefully — sessions just run unsandboxed
    }

    [Fact]
    public async Task GetCapabilities_reports_sandbox_provider_available_when_one_is_registered()
    {
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(new FakeSandboxProvider()));

        var capabilities = manager.GetCapabilities();

        var sandbox = Assert.Single(capabilities, c => c.Id == HostCapabilityIds.SandboxProvider);
        Assert.True(sandbox.Available);
    }

    [Fact]
    public async Task GetCapabilities_reports_agent_adapter_available_whenever_any_are_registered()
    {
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var capabilities = manager.GetCapabilities();

        var agents = Assert.Single(capabilities, c => c.Id == HostCapabilityIds.AgentAdapter);
        Assert.True(agents.Available);
        Assert.True(agents.FailClosed); // no adapters at all would mean no session can ever open
    }

    [Fact]
    public async Task Restored_sandboxed_session_resumes_on_prompt_by_reattaching_its_vm()
    {
        var store = new InMemoryEventStore();
        var regPath = Path.Combine(Path.GetTempPath(), "agnes-sbxreg-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            string sessionId;
            await using (var manager = new SessionManager(
                TestPluginRegistries.Agents(new ScriptedAgentAdapter()), store, new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(new FakeSandboxProvider()), [new FakeCredentialProvider()],
                sandboxRegistry: new SandboxRegistry(regPath)))
            {
                var info = await manager.OpenSessionAsync("scripted", "/tmp/work");
                sessionId = info.SessionId;
                Assert.NotNull(info.Sandbox); // provisioned + persisted to the registry file
            }

            // ---- host "restart": a new manager over the same store + registry file, no live VM handles ----
            var adapter2 = new ScriptedAgentAdapter();
            adapter2.Session.OnPrompt = (_, s) => { s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };
            var sandboxes2 = new FakeSandboxProvider();
            await using var resumed = new SessionManager(
                TestPluginRegistries.Agents(adapter2), store, new NullBroadcaster(), NullLoggerFactory.Instance,
                TestPluginRegistries.Sandboxes(sandboxes2), [new FakeCredentialProvider()],
                sandboxRegistry: new SandboxRegistry(regPath)); // loads the persisted VM record
            await resumed.RestoreAsync();

            // While dormant (not yet resumed), the snapshot still reports the sandbox from the registry so
            // the client shows the chip (as stopped) rather than hiding it.
            var dormant = await resumed.GetSnapshotAsync(sessionId, 0);
            Assert.NotNull(dormant.Session.Sandbox);
            Assert.Equal("Stopped", dormant.Session.Sandbox!.State);

            // Prompting a restored sandboxed session now re-attaches its VM (by name) instead of erroring.
            await resumed.PromptAsync(sessionId, [new TextContent("continue")]);

            // After the resume the live status flips to running.
            var live = resumed.GetSandboxStatus(sessionId);
            Assert.NotNull(live);
            Assert.Equal("Running", live!.State);

            Assert.NotNull(adapter2.LastOptions);
            Assert.NotNull(adapter2.LastOptions!.Sandbox); // relaunched INSIDE the re-attached sandbox
            Assert.Equal("/work", adapter2.LastOptions.WorkingDirectory);
        }
        finally
        {
            try { File.Delete(regPath); } catch { /* best effort */ }
        }
    }
}
