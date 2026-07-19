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
