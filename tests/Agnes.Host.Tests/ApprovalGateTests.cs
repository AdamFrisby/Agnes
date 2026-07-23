using System.Diagnostics;
using Agnes.Abstractions;
using Agnes.Host.Approvals;
using Agnes.Host.Events;
using Agnes.Host.Git;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Generic approval-gated actions (notifications/02 tier 2): the gating table decides execute-now vs
/// require-approval, a gated invocation becomes a durable <see cref="ApprovalRequest"/> that shows up in the
/// same inbox as tier 1, and resolution runs (approve) or turns down (reject) the parked action — with the two
/// real callers (GitCommit, credential share) staying immediate on the default, ungated surface.
/// </summary>
public class ApprovalGateTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    // A fake gated action that records how many times it actually ran, so tests can assert execute-vs-not.
    private sealed class RecordingAction : IApprovalGatedAction
    {
        private readonly bool _throws;
        private int _runs;

        public RecordingAction(string id = "test.action", bool throws = false)
        {
            ActionId = id;
            _throws = throws;
        }

        public string ActionId { get; }
        public string Summary => "do the thing";
        public string? Preview => null;
        public int Runs => Volatile.Read(ref _runs);

        public Task ExecuteAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            return _throws ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    private static ApprovalGateService GatedService(params (string ActionId, ApprovalSurface Surface)[] gated)
        => new(new ApprovalGate(gated), new ApprovalRequestStore(null));

    [Fact]
    public async Task Gated_surface_creates_open_request_and_does_not_execute()
    {
        var service = GatedService(("test.action", ApprovalSurface.SessionAgent));
        var action = new RecordingAction();

        var request = await service.InvokeAsync(action, ApprovalSurface.SessionAgent);

        Assert.NotNull(request);
        Assert.Equal(ApprovalStatus.Open, request!.Status);
        Assert.Equal(0, action.Runs); // parked, not run.
        Assert.Single(service.ListOpen());
    }

    [Fact]
    public async Task Ungated_surface_executes_immediately_with_no_request()
    {
        // The action is gated for SessionAgent only; invoking from the (ungated) Client surface runs it now.
        var service = GatedService(("test.action", ApprovalSurface.SessionAgent));
        var action = new RecordingAction();

        var request = await service.InvokeAsync(action, ApprovalSurface.Client);

        Assert.Null(request);
        Assert.Equal(1, action.Runs);
        Assert.Empty(service.ListOpen());
    }

    [Fact]
    public async Task Approving_runs_the_action_and_marks_executed()
    {
        var service = GatedService(("test.action", ApprovalSurface.SessionAgent));
        var action = new RecordingAction();
        var request = await service.InvokeAsync(action, ApprovalSurface.SessionAgent);

        var resolved = await service.ApproveAsync(request!.Id);

        Assert.Equal(1, action.Runs);
        Assert.Equal(ApprovalStatus.Executed, resolved!.Status);
        Assert.Empty(service.ListOpen());
    }

    [Fact]
    public async Task Approving_a_throwing_action_marks_failed()
    {
        var service = GatedService(("test.action", ApprovalSurface.SessionAgent));
        var action = new RecordingAction(throws: true);
        var request = await service.InvokeAsync(action, ApprovalSurface.SessionAgent);

        var resolved = await service.ApproveAsync(request!.Id);

        Assert.Equal(1, action.Runs);
        Assert.Equal(ApprovalStatus.Failed, resolved!.Status);
    }

    [Fact]
    public async Task Rejecting_never_runs_the_action_and_marks_rejected()
    {
        var service = GatedService(("test.action", ApprovalSurface.SessionAgent));
        var action = new RecordingAction();
        var request = await service.InvokeAsync(action, ApprovalSurface.SessionAgent);

        var resolved = service.Reject(request!.Id);

        Assert.Equal(0, action.Runs);
        Assert.Equal(ApprovalStatus.Rejected, resolved!.Status);
        Assert.Empty(service.ListOpen());
    }

    [Fact]
    public void Open_gated_request_survives_a_store_reload()
    {
        var file = Path.Combine(Path.GetTempPath(), $"agnes-approvals-{Guid.NewGuid():n}.json");
        try
        {
            ApprovalRequest created;
            {
                var store = new ApprovalRequestStore(file);
                created = store.Create("test.action", ApprovalSurface.SessionAgent, "do the thing", preview: null);
            }

            // A brand-new store over the same file — as if the host restarted — still sees the open request.
            var reloaded = new ApprovalRequestStore(file);
            var again = reloaded.Get(created.Id);

            Assert.NotNull(again);
            Assert.Equal(ApprovalStatus.Open, again!.Status);
            Assert.Equal("do the thing", again.ArgsSummary);
            Assert.Single(reloaded.ListOpen());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Open_gated_request_appears_in_the_inbox_alongside_a_session_permission()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        var service = GatedService(("test.action", ApprovalSurface.SessionAgent));
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance,
            approvals: service);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        // A live session permission (tier 1) plus a gated action (tier 2) — both must land in one inbox.
        await EmitPermissionAsync(manager, adapter, store, info.SessionId, "req-open", "tool-1", "Run tests");
        var gated = await service.InvokeAsync(new RecordingAction(), ApprovalSurface.SessionAgent);

        var approvals = await manager.GetOpenApprovalsAsync();

        Assert.Equal(2, approvals.Count);
        Assert.Contains(approvals, a => a.Kind == OpenApprovalKind.SessionPermission && a.RequestId == "req-open");
        var row = Assert.Single(approvals, a => a.Kind == OpenApprovalKind.GatedAction);
        Assert.Equal(gated!.Id, row.RequestId);
        Assert.Equal("do the thing", row.Title);
        Assert.Equal("test.action", row.Source);
    }

    [Fact]
    public async Task Gated_git_commit_does_not_commit_until_approved()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"agnes-gate-commit-{Guid.NewGuid():n}");
        Directory.CreateDirectory(repo);
        try
        {
            await RunGitAsync(repo, "init");
            await RunGitAsync(repo, "config", "user.email", "t@example.com");
            await RunGitAsync(repo, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "hello");

            var git = new GitService();
            var service = GatedService((GitCommitAction.Id, ApprovalSurface.SessionAgent));
            var adapter = new ScriptedAgentAdapter();
            await using var manager = new SessionManager(
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(),
                NullLoggerFactory.Instance, approvals: service);

            var info = await manager.OpenSessionAsync("scripted", repo, useSandbox: false);

            // The agent (SessionAgent surface) asks to commit — gated, so it must not commit yet.
            var pending = await manager.GitCommitAsync(info.SessionId, "add a.txt", ApprovalSurface.SessionAgent);
            Assert.False(pending.Success);
            Assert.Contains("approval", pending.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True((await git.GetStatusAsync(repo)).IsDirty); // still uncommitted while the request is open.

            var open = Assert.Single(service.ListOpen());

            // Approve from the inbox: now the commit actually runs.
            await manager.ResolveGatedApprovalAsync(open.Id, approve: true);

            Assert.False((await git.GetStatusAsync(repo)).IsDirty); // committed.
            Assert.Equal(ApprovalStatus.Executed, service.Get(open.Id)!.Status);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task Ungated_git_commit_commits_immediately()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"agnes-ungated-commit-{Guid.NewGuid():n}");
        Directory.CreateDirectory(repo);
        try
        {
            await RunGitAsync(repo, "init");
            await RunGitAsync(repo, "config", "user.email", "t@example.com");
            await RunGitAsync(repo, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "hello");

            var git = new GitService();
            // git.commit gated only for SessionAgent; a Client-surface (or default) commit is immediate.
            var service = GatedService((GitCommitAction.Id, ApprovalSurface.SessionAgent));
            var adapter = new ScriptedAgentAdapter();
            await using var manager = new SessionManager(
                TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(),
                NullLoggerFactory.Instance, approvals: service);

            var info = await manager.OpenSessionAsync("scripted", repo, useSandbox: false);

            var result = await manager.GitCommitAsync(info.SessionId, "add a.txt"); // default Client surface.

            Assert.True(result.Success);
            Assert.False((await git.GetStatusAsync(repo)).IsDirty);
            Assert.Empty(service.ListOpen());
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static async Task<int> RunGitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    private static async Task EmitPermissionAsync(SessionManager manager, ScriptedAgentAdapter adapter, IEventStore store, string sessionId, string requestId, string toolCallId, string title)
    {
        adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new PermissionRequestedEvent(requestId, toolCallId, title, [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce)]));
            return Task.FromResult(StopReason.EndTurn);
        };

        var before = await store.GetHeadAsync(sessionId);
        await manager.PromptAsync(sessionId, [new TextContent("go")]);
        var expected = before + 2; // the prompt event + the permission event.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await store.GetHeadAsync(sessionId) < expected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
