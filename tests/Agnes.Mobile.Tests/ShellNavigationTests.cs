using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The mobile shell owns one thing the desktop client never has to think about: a single back gesture
/// that has to mean the right thing at four different depths. These pin that contract down.
/// </summary>
public sealed class ShellNavigationTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public ShellNavigationTests() => JsonStore.UseDirectory(_state);

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
    public void Back_closes_the_sheet_before_it_pops_a_page()
    {
        var shell = NewShell();
        shell.Push(new AboutPageViewModel(shell));
        shell.ShowSheet(new HostsSheetViewModel(shell, shell.Hosts, shell.Sessions));

        Assert.True(shell.GoBack());

        Assert.Null(shell.CurrentSheet);
        Assert.NotNull(shell.CurrentPage); // the page is still there — one gesture, one level
    }

    [Fact]
    public void Back_pops_pages_one_at_a_time()
    {
        var shell = NewShell();
        shell.Push(new AboutPageViewModel(shell));
        shell.Push(new AppearancePageViewModel(shell));

        Assert.True(shell.GoBack());
        Assert.IsType<AboutPageViewModel>(shell.CurrentPage);

        Assert.True(shell.GoBack());
        Assert.Null(shell.CurrentPage);
    }

    [Fact]
    public void Back_from_another_tab_returns_to_sessions_rather_than_leaving()
    {
        var shell = NewShell();
        shell.SelectTab(ShellTab.More);

        Assert.True(shell.GoBack());

        Assert.Equal(ShellTab.Sessions, shell.Tab);
    }

    [Fact]
    public void Back_at_the_root_of_the_first_tab_lets_the_system_leave_the_app()
    {
        var shell = NewShell();

        // Nothing left to unwind: the shell must decline so Android's own back can take the app off screen.
        Assert.False(shell.GoBack());
    }

    [Fact]
    public void Switching_tab_unwinds_the_stack_so_a_destination_is_always_a_fresh_start()
    {
        var shell = NewShell();
        shell.Push(new AboutPageViewModel(shell));

        shell.SelectTab(ShellTab.Search);

        Assert.Null(shell.CurrentPage);
        Assert.Empty(shell.Stack);
    }

    [Fact]
    public void The_tab_bar_hides_while_a_page_is_pushed()
    {
        var shell = NewShell();
        Assert.True(shell.ShowTabs);

        shell.Push(new AboutPageViewModel(shell));

        Assert.False(shell.ShowTabs);
    }

    [Fact]
    public void A_page_gets_first_refusal_on_back()
    {
        var shell = NewShell();
        var page = new SelfHandlingPage();
        shell.Push(page);

        Assert.True(shell.GoBack());
        Assert.Same(page, shell.CurrentPage); // it handled the gesture itself; nothing popped
    }

    private sealed class SelfHandlingPage : PageViewModel
    {
        public override string Title => "Handles back";

        public override bool OnBackRequested() => true;
    }
}
