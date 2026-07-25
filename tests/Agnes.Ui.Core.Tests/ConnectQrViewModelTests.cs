using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The session QR panel. Its failure modes are unusually easy to ship blind, because the thing that
/// invokes it is a menu item whose enabled state is derived from this view model: if a request never
/// finishes, or finishes with an error nothing displays, the visible result is a menu item that greys
/// out and a feature that appears to do nothing at all. That is exactly how it failed in the field, so
/// these pin the states rather than the happy path.
/// </summary>
public sealed class ConnectQrViewModelTests
{
    /// <summary>Port 1 on loopback: reliably refused, no network round trip.</summary>
    private const string DeadHost = "http://127.0.0.1:1";

    private static ConnectQrViewModel ForHost(string? url, Func<string?>? sessionId = null)
        => new(() => url is null ? null : (url, "a-token"),
            sessionId ?? (() => "sess-1"),
            ImmediateDispatcher.Instance);

    [Fact]
    public async Task A_host_that_refuses_leaves_the_menu_item_usable_and_says_why()
    {
        var vm = ForHost(DeadHost);

        await vm.ShowCommand.ExecuteAsync(null);

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsVisible);
        Assert.True(vm.HasError);
        Assert.True(vm.IsPanelOpen);                    // the failure is on screen, not swallowed
        Assert.True(vm.ShowCommand.CanExecute(null));   // and you can try again
    }

    [Fact]
    public async Task With_no_host_yet_it_says_so_rather_than_doing_nothing()
    {
        var vm = ForHost(null);

        await vm.ShowCommand.ExecuteAsync(null);

        Assert.True(vm.IsPanelOpen);
        Assert.Contains("host", vm.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dismissing_an_error_closes_the_panel_and_clears_it()
    {
        var vm = ForHost(DeadHost);
        await vm.ShowCommand.ExecuteAsync(null);

        Assert.True(vm.HideCommand.CanExecute(null));   // the panel must be dismissible when it's showing
        await vm.HideCommand.ExecuteAsync(null);

        Assert.False(vm.IsPanelOpen);
        Assert.False(vm.HasError);
        Assert.True(vm.ShowCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_session_is_read_when_the_code_is_asked_for_not_when_the_tab_is_built()
    {
        // This view model is constructed when its tab's view loads, which is before the session exists.
        // Capturing the id then captures null every time, and the QR silently pairs the phone to the host
        // without opening anything — a failure with no symptom at all on the desktop side.
        string? sessionId = null;
        var reads = 0;
        var vm = ForHost(DeadHost, () => { reads++; return sessionId; });

        Assert.Equal(0, reads);

        sessionId = "sess-42";
        await vm.ShowCommand.ExecuteAsync(null);

        Assert.Equal(1, reads);
    }

    [Fact]
    public void Nothing_is_minted_until_asked()
    {
        // Constructing the panel must not reach the host: the code it would fetch is a credential, and
        // the view model is built for every session tab whether or not anyone opens the menu.
        var reached = false;
        var vm = new ConnectQrViewModel(
            () => { reached = true; return (DeadHost, "a-token"); },
            () => "sess-1",
            ImmediateDispatcher.Instance);

        Assert.False(reached);
        Assert.False(vm.IsPanelOpen);
        Assert.True(vm.ShowCommand.CanExecute(null));
        Assert.False(vm.HideCommand.CanExecute(null));
    }
}
