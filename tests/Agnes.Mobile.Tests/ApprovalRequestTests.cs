using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Protocol;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The connect screen's "ask a device you already use" path. The flow itself is covered end-to-end
/// against a real host in <c>Agnes.Integration.Tests</c>; what matters here is what the phone shows
/// while it waits, and that a host which isn't there says so rather than leaving the screen stuck on
/// six digits nobody will ever approve.
/// </summary>
public sealed class ApprovalRequestTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public ApprovalRequestTests() => JsonStore.UseDirectory(_state);

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
    public async Task An_address_with_nothing_behind_it_reports_the_address_not_a_pairing_problem()
    {
        var shell = NewShell();
        var connect = new ConnectPageViewModel(shell, shell.Hosts, shell.Sessions);
        connect.Address = "http://127.0.0.1:1"; // reliably refused, no network round trip

        await connect.AskApprovalCommand.ExecuteAsync(null);

        Assert.True(connect.StatusIsError);
        Assert.False(connect.IsAwaitingApproval);  // no digits left on screen to compare against nothing
        Assert.False(connect.IsBusy);
        Assert.Contains("reach", connect.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code", connect.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancelling_takes_the_digits_off_the_screen()
    {
        var shell = NewShell();
        var connect = new ConnectPageViewModel(shell, shell.Hosts, shell.Sessions)
        {
            IsAwaitingApproval = true,
            VerificationCode = "418302",
        };

        connect.CancelApprovalCommand.Execute(null);

        // A request you walked away from must not keep polling, and must not keep showing digits that
        // someone could still be talked into approving.
        Assert.False(connect.IsAwaitingApproval);
    }

    [Fact]
    public void The_digits_shown_are_six_and_stable_for_a_given_request()
    {
        // The phone derives them itself from the key it presented; the same inputs must always give the
        // same digits, or the two screens would never agree.
        const string key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE-not-a-real-key";

        var first = PairVerification.Derive(key, "req-1");

        Assert.Equal(first, PairVerification.Derive(key, "req-1"));
        Assert.NotEqual(first, PairVerification.Derive(key, "req-2"));
        Assert.Matches("^[0-9]{6}$", first);
    }
}
