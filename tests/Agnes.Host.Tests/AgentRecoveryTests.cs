using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Sandbox;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class AgentRecoveryTests
{
    private sealed class CollectingBroadcaster : ISessionBroadcaster
    {
        public ConcurrentQueue<(string SessionId, SessionEvent Event)> Published { get; } = new();

        public Task PublishAsync(string sessionId, SessionEvent @event)
        {
            Published.Enqueue((sessionId, @event));
            return Task.CompletedTask;
        }
    }

    /// <summary>An adapter that hands out a fresh session each launch and records the launch options.</summary>
    private sealed class RelaunchableAdapter(string id = "scripted") : IAgentAdapter
    {
        public List<ScriptedAgentSession> Sessions { get; } = [];
        public List<AgentSessionOptions> Starts { get; } = [];

        public AgentDescriptor Descriptor { get; } = new() { Id = id, DisplayName = "Agent" };

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            Starts.Add(options);
            var session = new ScriptedAgentSession { AgentSessionId = "scripted" };
            Sessions.Add(session);
            return Task.FromResult<IAgentSession>(session);
        }
    }

    private sealed class FakeSandbox : ISandbox
    {
        public string Id { get; } = "fake-vm";
        public string HomeDirectory => "/home/agnes";
        public SandboxInfo Info => new("fake", Id, SandboxState.Running);
        public (string Command, IReadOnlyList<string> Arguments) WrapCommand(string command, IReadOnlyList<string> arguments, string workingDirectory) => (command, arguments);
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default) => Task.FromResult(new SandboxExecResult(0, "", ""));
        public Task MaterializeCredentialAsync(SandboxCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        public string Name => "fake";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken = default) => Task.FromResult<ISandbox>(new FakeSandbox());
        public Task<IReadOnlyList<SandboxInfo>> ListManagedAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SandboxInfo>>([]);
        public Task<ISandbox> AttachAsync(string vmName, SandboxSpec spec, bool start, CancellationToken cancellationToken = default) => Task.FromResult<ISandbox>(new FakeSandbox());
    }

    private sealed class FakeClaudeCredentialProvider : IAgentCredentialProvider
    {
        public bool Handles(string adapterId) => adapterId == "claude-code-native";
        public Task<SandboxCredential> GetAsync(string adapterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxCredential { EnvironmentVariables = new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "fresh" } });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private const string ResumableId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Agent_crash_auto_restarts_and_resumes_the_conversation()
    {
        var adapter = new RelaunchableAdapter();
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster, NullLoggerFactory.Instance);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        // The agent reports its real (resumable) session id the way a native CLI does — an init line
        // mapped to a SessionStartedEvent — which the host persists to the catalogue for --resume.
        adapter.Sessions[0].Emit(new SessionStartedEvent(ResumableId));
        await WaitForAsync(() => broadcaster.Published.Any(p => p.Event is SessionStartedEvent));
        await Task.Delay(50); // let the (fire-and-forget) catalogue persist settle

        // The CLI process dies mid-session.
        adapter.Sessions[0].Die();

        // The host detects it and relaunches, resuming the conversation with --resume <id>.
        await WaitForAsync(() => adapter.Starts.Count == 2);
        Assert.Equal(ResumableId, adapter.Starts[1].ResumeSessionId);

        // The user sees what happened.
        await WaitForAsync(() => broadcaster.Published.Any(p => p.Event is NoticeEvent n && n.Message.Contains("restarting", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(broadcaster.Published, p => p.Event is NoticeEvent n && n.Message == "Agent restarted.");
    }

    [Fact]
    public async Task Repeated_crashes_pause_auto_restart_until_a_manual_restart()
    {
        var adapter = new RelaunchableAdapter();
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster, NullLoggerFactory.Instance);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        // First crash → one auto-restart.
        adapter.Sessions[0].Die();
        await WaitForAsync(() => adapter.Starts.Count == 2);

        // Second crash right after → the debounce trips, so NO third auto-launch; the user is asked to restart.
        adapter.Sessions[1].Die();
        await WaitForAsync(() => broadcaster.Published.Any(p =>
            p.Event is NoticeEvent { IsError: true } n && n.Message.Contains("Restart agent", StringComparison.Ordinal)));
        await Task.Delay(100);
        Assert.Equal(2, adapter.Starts.Count); // did not auto-relaunch a third time

        // A manual restart forces a relaunch and clears the pause.
        await manager.RestartAgentAsync(info.SessionId);
        Assert.Equal(3, adapter.Starts.Count);
    }

    [Fact]
    public async Task Revoked_claude_oauth_token_relaunches_with_fresh_credentials()
    {
        var adapter = new RelaunchableAdapter("claude-code-native");
        var broadcaster = new CollectingBroadcaster();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster, NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(new FakeSandboxProvider()), [new FakeClaudeCredentialProvider()]);

        var info = await manager.OpenSessionAsync("claude-code-native", "/tmp/work");
        Assert.Single(adapter.Starts);

        // The sandboxed agent's OAuth token was revoked (host rotated it during a long idle).
        adapter.Sessions[0].Emit(new AgentErrorEvent("API Error: 401 OAuth access token has been revoked"));

        // The host relaunches the agent with freshly-materialized credentials and tells the user to resend.
        await WaitForAsync(() => adapter.Starts.Count == 2);
        Assert.True(adapter.Starts[1].Sandbox is not null);
        await WaitForAsync(() => broadcaster.Published.Any(p =>
            p.Event is NoticeEvent n && n.Message.Contains("refreshed credentials", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_non_auth_agent_error_does_not_trigger_a_relaunch()
    {
        var adapter = new RelaunchableAdapter("claude-code-native");
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance,
            TestPluginRegistries.Sandboxes(new FakeSandboxProvider()), [new FakeClaudeCredentialProvider()]);

        var info = await manager.OpenSessionAsync("claude-code-native", "/tmp/work");
        adapter.Sessions[0].Emit(new AgentErrorEvent("I can't help with that request."));

        await Task.Delay(200);
        Assert.Single(adapter.Starts); // an ordinary error is not an auth failure — no relaunch
    }
}
