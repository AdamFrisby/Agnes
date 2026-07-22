using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
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
    private sealed class RelaunchableAdapter : IAgentAdapter
    {
        public List<ScriptedAgentSession> Sessions { get; } = [];
        public List<AgentSessionOptions> Starts { get; } = [];

        public AgentDescriptor Descriptor { get; } = new() { Id = "scripted", DisplayName = "Scripted Agent" };

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            Starts.Add(options);
            var session = new ScriptedAgentSession { AgentSessionId = "scripted" };
            Sessions.Add(session);
            return Task.FromResult<IAgentSession>(session);
        }
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
}
