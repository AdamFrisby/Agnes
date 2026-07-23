using System.Diagnostics;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Agent commit-message generation (git-and-files/01): summarizes the staged diff through the shared one-shot
/// primitive and returns the suggestion; an empty staged diff yields a clear "no suggestion" rather than a crash.
/// </summary>
public class CommitMessageGenerationTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    [Fact]
    public async Task Generates_a_message_from_the_staged_diff()
    {
        if (!AgentCommand.IsOnPath("git"))
        {
            return; // no git — can't stage a diff to summarize
        }

        var repo = Path.Combine(Path.GetTempPath(), "agnes-commitmsg-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init");
            Git(repo, "config", "user.email", "t@example.com");
            Git(repo, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "hello.txt"), "hello world");
            Git(repo, "add", "-A"); // stage it, so `git diff --cached` is non-empty

            var adapter = new ScriptedAgentAdapter();
            adapter.Session.OnPrompt = (_, session) =>
            {
                session.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("feat: add greeting file")));
                session.Emit(new TurnEndedEvent(StopReason.EndTurn));
                return Task.FromResult(StopReason.EndTurn);
            };

            await using var manager = new SessionManager(
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);
            var info = await manager.OpenSessionAsync("scripted", repo, useSandbox: false);

            var suggestion = await manager.GenerateCommitMessageAsync(info.SessionId);

            Assert.True(suggestion.HasSuggestion);
            Assert.Equal("feat: add greeting file", suggestion.Message);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task An_empty_staged_diff_yields_no_suggestion_without_running_the_agent()
    {
        if (!AgentCommand.IsOnPath("git"))
        {
            return;
        }

        var repo = Path.Combine(Path.GetTempPath(), "agnes-commitmsg-empty-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init");
            Git(repo, "config", "user.email", "t@example.com");
            Git(repo, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "hello.txt"), "hello world");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-m", "initial"); // now nothing is staged

            var adapter = new ScriptedAgentAdapter(); // its default prompt handler must never be invoked
            await using var manager = new SessionManager(
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);
            var info = await manager.OpenSessionAsync("scripted", repo, useSandbox: false);

            var suggestion = await manager.GenerateCommitMessageAsync(info.SessionId);

            Assert.False(suggestion.HasSuggestion);
            Assert.Equal(string.Empty, suggestion.Message);
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
