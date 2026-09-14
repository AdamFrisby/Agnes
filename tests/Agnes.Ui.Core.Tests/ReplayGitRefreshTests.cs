using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// Replaying a session's history asks the host for its git status once, at the end — not once per turn
/// the session ever finished. One live session replayed sixty-one turn ends on every open, sent sixty-one
/// git-status requests, and stayed reachable from sixty-one pending calls after its tab had been put to
/// sleep.
/// </summary>
public class ReplayGitRefreshTests
{
    private sealed class CountingHost : StubAgnesHost
    {
        public int GitStatusCalls;

        public override Task<GitStatus> GetGitStatusAsync(string sessionId)
        {
            Interlocked.Increment(ref GitStatusCalls);
            return Task.FromResult(new GitStatus(false, null, false, []));
        }
    }

    [Fact]
    public async Task A_replayed_history_refreshes_git_once_and_a_live_turn_end_refreshes_again()
    {
        var host = new CountingHost();
        var view = new SessionView("s1");
        var history = Enumerable.Range(1, 61)
            .Select(i => (SessionEvent)new TurnEndedEvent(StopReason.EndTurn) { Sequence = i })
            .ToList();
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 61), history, 61));

        await using var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        Assert.Equal(1, host.GitStatusCalls);

        view.Apply(new TurnEndedEvent(StopReason.EndTurn) { Sequence = 62 });
        Assert.Equal(2, host.GitStatusCalls);
    }
}
