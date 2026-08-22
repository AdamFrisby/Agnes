using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Client;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The sessions list is the app's front page, and its whole job is to put what needs a human at the
/// top. These cover that ordering and the reattach path a phone takes every time it wakes up.
/// </summary>
public sealed class SessionListTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public SessionListTests() => JsonStore.UseDirectory(_state);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static ShellViewModel NewShell()
        => new(new MobileConnector(), ImmediateDispatcher.Instance, new MobileSettings(), "Test device");

    [Fact]
    public async Task A_first_launch_seeds_the_offline_demo_so_the_app_is_never_an_empty_room()
    {
        var shell = NewShell();

        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0);

        var entry = Assert.Single(shell.Sessions.All);
        Assert.True(DemoHost.IsDemo(entry.Saved.HostUrl));
    }

    [Fact]
    public async Task The_demo_is_seeded_only_once()
    {
        var shell = NewShell();
        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0);

        // A second launch with the flag already recorded must not pile up another demo session.
        var again = NewShell();
        await again.StartAsync();
        await WaitFor(() => again.Sessions.All.Count > 0);

        Assert.Single(again.Sessions.All);
    }

    [Fact]
    public async Task Sessions_come_back_after_a_relaunch()
    {
        var shell = NewShell();
        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0);
        var sessionId = shell.Sessions.All[0].SessionId;

        // A new shell reads the device's saved pointers and resubscribes — the host holds the history.
        var relaunched = NewShell();
        await relaunched.StartAsync();

        var restored = Assert.Single(relaunched.Sessions.All);
        Assert.Equal(sessionId, restored.SessionId);
        await WaitFor(() => restored.IsLive);
        Assert.True(restored.IsLive);
    }

    [Fact]
    public async Task Forgetting_a_session_removes_it_from_this_device_only()
    {
        var shell = NewShell();
        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0);

        shell.Sessions.Forget(shell.Sessions.All[0]);

        Assert.Empty(shell.Sessions.All);
        Assert.Empty(SessionRegistry.Load());
    }

    [Fact]
    public void The_host_chip_names_the_demo_rather_than_claiming_nothing_is_connected()
    {
        var shell = NewShell();

        // Nothing paired yet: saying "no host" on a first launch is both wrong and discouraging.
        Assert.DoesNotContain("No host", shell.Sessions.HostSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_session_is_surfaced_in_the_inbox()
    {
        var shell = NewShell();
        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0 && shell.Sessions.All[0].IsLive);

        var session = shell.Sessions.All[0].Session!;
        session.PromptText = "Delete the build directory."; // the simulated agent asks before destroying
        session.SendCommand.Execute(null);
        await WaitFor(() => session.PendingPermission is not null);

        await shell.Inbox.RefreshAsync();

        var blocker = Assert.Single(shell.Inbox.Blocked);
        Assert.Equal("Approval", blocker.Kind);
        Assert.True(blocker.CanAnswerHere); // answerable without opening the session
    }

    [Fact]
    public async Task Answering_from_the_inbox_unblocks_the_agent()
    {
        var shell = NewShell();
        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0 && shell.Sessions.All[0].IsLive);

        var session = shell.Sessions.All[0].Session!;
        session.PromptText = "Delete the build directory.";
        session.SendCommand.Execute(null);
        await WaitFor(() => session.PendingPermission is not null);
        await shell.Inbox.RefreshAsync();

        shell.Inbox.AllowCommand.Execute(shell.Inbox.Blocked[0]);
        await WaitFor(() => session.PendingPermission is null);

        Assert.Null(session.PendingPermission);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }

    // ---- discovery: the host owns the session list, not the device ----

    [Fact]
    public void A_dismissed_session_stays_dismissed_across_relaunches()
    {
        // "Remove from this device" has to outlive the process, or discovery re-adds it on next launch.
        DismissedSessions.Add("sess-1");

        Assert.Contains("sess-1", DismissedSessions.Load());
    }

    [Fact]
    public void Dismissing_is_idempotent_and_reversible()
    {
        DismissedSessions.Add("sess-1");
        DismissedSessions.Add("sess-1");
        Assert.Single(DismissedSessions.Load());

        DismissedSessions.Remove("sess-1");
        Assert.Empty(DismissedSessions.Load());
    }

    [Fact]
    public async Task Forgetting_a_session_records_it_so_discovery_will_not_resurrect_it()
    {
        // Discovery lists what the HOST has. Without a record of the dismissal, forgetting a session
        // would undo itself on the very next pull-to-refresh.
        var shell = NewShell();
        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0);
        var forgotten = shell.Sessions.All[0].SessionId;

        shell.Sessions.Forget(shell.Sessions.All[0]);

        Assert.Contains(forgotten, DismissedSessions.Load());
    }

    [Fact]
    public async Task Refreshing_does_not_resurrect_a_forgotten_session()
    {
        var shell = NewShell();
        await shell.StartAsync();
        await WaitFor(() => shell.Sessions.All.Count > 0);
        shell.Sessions.Forget(shell.Sessions.All[0]);

        await shell.Sessions.RefreshAsync();

        Assert.Empty(shell.Sessions.All);
    }
}
