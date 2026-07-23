using System.Diagnostics;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Changed-file scoping (git-and-files/01): the host query answers "this turn" / "this session" from the
/// event-sourced tool-call log and "whole repo" from git status, each exactly — no over- or under-inclusion.
/// </summary>
public class ChangedFileScopingTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static SessionManager NewManager(IEventStore store)
        => new(TestPluginRegistries.Agents(new ScriptedAgentAdapter()), store, new NullBroadcaster(), NullLoggerFactory.Instance);

    private static ToolCallEvent Edit(string workingDirectory, params string[] files)
        => new("call-" + Guid.NewGuid().ToString("n"), "Edit", ToolKind.Edit, ToolCallStatus.Completed,
            files.Select(f => (ContentBlock)new DiffContent(Path.Combine(workingDirectory, f), null, "changed")).ToArray());

    [Fact]
    public async Task This_turn_and_this_session_scopes_match_exactly_the_files_the_tool_calls_touched()
    {
        var store = new InMemoryEventStore();
        await using var manager = NewManager(store);
        var dir = Path.Combine(Path.GetTempPath(), "agnes-scope-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var info = await manager.OpenSessionAsync("scripted", dir, useSandbox: false);

            // Turn 1 touches a + b; turn 2 touches b (overlap) + c (disjoint).
            await store.AppendAsync(info.SessionId, Edit(dir, "a.txt", "b.txt"));
            await store.AppendAsync(info.SessionId, new TurnEndedEvent(StopReason.EndTurn));
            await store.AppendAsync(info.SessionId, Edit(dir, "b.txt", "c.txt"));
            await store.AppendAsync(info.SessionId, new TurnEndedEvent(StopReason.EndTurn));

            var thisTurn = await manager.GetChangedFilesAsync(info.SessionId, ChangedFileScope.ThisTurn);
            var thisSession = await manager.GetChangedFilesAsync(info.SessionId, ChangedFileScope.ThisSession);

            Assert.Equal(new[] { "b.txt", "c.txt" }, thisTurn);
            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, thisSession);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task This_turn_covers_an_in_progress_turn_after_the_last_completed_one()
    {
        var store = new InMemoryEventStore();
        await using var manager = NewManager(store);
        var dir = Path.Combine(Path.GetTempPath(), "agnes-scope-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var info = await manager.OpenSessionAsync("scripted", dir, useSandbox: false);

            await store.AppendAsync(info.SessionId, Edit(dir, "a.txt"));
            await store.AppendAsync(info.SessionId, new TurnEndedEvent(StopReason.EndTurn));
            // A new turn is under way (no closing TurnEndedEvent yet): it is the current turn.
            await store.AppendAsync(info.SessionId, Edit(dir, "d.txt"));

            var thisTurn = await manager.GetChangedFilesAsync(info.SessionId, ChangedFileScope.ThisTurn);

            Assert.Equal(new[] { "d.txt" }, thisTurn);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Whole_repo_scope_is_the_git_status_change_set()
    {
        if (!AgentCommand.IsOnPath("git"))
        {
            return; // no git on this machine — the whole-repo scope can't be verified here
        }

        var store = new InMemoryEventStore();
        await using var manager = NewManager(store);
        var repo = Path.Combine(Path.GetTempPath(), "agnes-scope-repo-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init");
            await File.WriteAllTextAsync(Path.Combine(repo, "repo-only.txt"), "hi");

            var info = await manager.OpenSessionAsync("scripted", repo, useSandbox: false);
            // Tool-call events touch a different, on-disk-absent file — it must NOT leak into the whole-repo scope.
            await store.AppendAsync(info.SessionId, Edit(repo, "ghost.txt"));
            await store.AppendAsync(info.SessionId, new TurnEndedEvent(StopReason.EndTurn));

            var wholeRepo = await manager.GetChangedFilesAsync(info.SessionId, ChangedFileScope.WholeRepo);

            Assert.Equal(new[] { "repo-only.txt" }, wholeRepo);
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
