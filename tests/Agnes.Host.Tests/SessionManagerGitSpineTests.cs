using System.Diagnostics;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>A plugin bound to the spine can rewrite or veto a git commit
/// (see .ideas/00d-event-spine-and-ui-extensibility.md).</summary>
public class SessionManagerGitSpineTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private sealed class VetoCommit : IEventInterceptor<BeforeGitCommitEvent>
    {
        public ValueTask InterceptAsync(BeforeGitCommitEvent evt, CancellationToken ct = default) { evt.Cancel("policy"); return ValueTask.CompletedTask; }
    }

    private sealed class AppendTrailer : IEventInterceptor<BeforeGitCommitEvent>
    {
        public ValueTask InterceptAsync(BeforeGitCommitEvent evt, CancellationToken ct = default) { evt.Message += " [ci]"; return ValueTask.CompletedTask; }
    }

    private sealed class Record(List<string> sink) : IEventObserver<GitCommittedEvent>
    {
        public ValueTask ObserveAsync(GitCommittedEvent evt, CancellationToken ct = default) { sink.Add(evt.Message); return ValueTask.CompletedTask; }
    }

    private static SessionManager NewManager(EventBus bus)
        => new(TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);

    [Fact]
    public async Task A_plugin_can_veto_a_commit_without_touching_git()
    {
        var bus = new EventBus();
        await using var manager = NewManager(bus);
        var info = await manager.OpenSessionAsync("scripted", Path.GetTempPath(), useSandbox: false);
        bus.Intercept(new VetoCommit());

        var result = await manager.GitCommitAsync(info.SessionId, "anything");

        Assert.False(result.Success);
        Assert.Contains("blocked", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plugin_can_rewrite_the_commit_message_and_the_commit_is_observed()
    {
        if (!AgentCommand.IsOnPath("git"))
        {
            return; // no git on this machine — nothing to verify (keeps CI green where git is absent)
        }

        var repo = Path.Combine(Path.GetTempPath(), "agnes-gitspine-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init");
            Git(repo, "config", "user.email", "t@example.com");
            Git(repo, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "hello");

            var bus = new EventBus();
            await using var manager = NewManager(bus);
            var info = await manager.OpenSessionAsync("scripted", repo, useSandbox: false);
            var observed = new List<string>();
            bus.Intercept(new AppendTrailer());
            bus.Observe(new Record(observed));

            var result = await manager.GitCommitAsync(info.SessionId, "initial");

            Assert.True(result.Success, result.Message);
            Assert.Contains("initial [ci]", observed); // the observe event carries the plugin-rewritten message
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
