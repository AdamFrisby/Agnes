using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The screen a freshly paired phone lands on: what the host is already running, one tap to join it. The
/// phone's best trick is unblocking an agent started somewhere else, and that only works if the sessions
/// are offered rather than hunted for.
/// </summary>
public sealed class HostSessionsPageTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public HostSessionsPageTests() => JsonStore.UseDirectory(_state);

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

    private static async Task<HostSessionsPageViewModel> OpenAsync(ShellViewModel shell)
    {
        var link = shell.Hosts.Links.First();
        await link.ConnectAsync();
        var page = new HostSessionsPageViewModel(shell, shell.Hosts, shell.Sessions, link);
        shell.Push(page); // Push calls OnAppearing, which loads the catalogue
        await WaitFor(() => page.Catalog.HasSessions);
        return page;
    }

    [Fact]
    public async Task Lists_the_sessions_the_host_is_already_running()
    {
        var shell = NewShell();
        var page = await OpenAsync(shell);

        Assert.True(page.Catalog.HasSessions);
        Assert.All(page.Catalog.Sessions, r => Assert.False(string.IsNullOrEmpty(r.Title)));
    }

    [Fact]
    public async Task Joining_one_adopts_it_onto_this_device_and_opens_it()
    {
        var shell = NewShell();
        var page = await OpenAsync(shell);
        var row = page.Catalog.Sessions.First();

        page.OpenCommand.Execute(row);
        await WaitFor(() => shell.Sessions.All.Any(e => e.SessionId == row.SessionId));

        var entry = Assert.Single(shell.Sessions.All, e => e.SessionId == row.SessionId);
        Assert.Equal(row.AdapterId, entry.Saved.AdapterId);
        // Joining lands you in the session — that's what the tap asked for.
        Assert.IsType<SessionPageViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task A_session_this_device_already_has_is_reopened_rather_than_adopted_twice()
    {
        var shell = NewShell();
        var page = await OpenAsync(shell);
        var row = page.Catalog.Sessions.First();

        page.OpenCommand.Execute(row);
        await WaitFor(() => shell.Sessions.All.Any(e => e.SessionId == row.SessionId));
        var count = shell.Sessions.All.Count;

        shell.Pop();
        page.OpenCommand.Execute(row);
        await Task.Delay(100);

        Assert.Equal(count, shell.Sessions.All.Count);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }
}
