using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// Pairing with a host whose certificate the phone does not yet pin. Before this, a typed address on a
/// self-signed host failed on trust before any request was sent and the screen said "can't reach that
/// address" — which is the friction the operator kept hitting. The phone now does what the CLI does:
/// learns the key the host presents, shows it, pins it, and refuses a host whose key has changed.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TrustOnFirstUseTests : IDisposable
{
    private const string Pin = "01f9d48913f5a642cc0b350b182c960adc48348c73828b11f1ad68a43b71bfeb";
    private const string Other = "ffffffff13f5a642cc0b350b182c960adc48348c73828b11f1ad68a43b71bfeb";
    private const string Url = "https://127.0.0.1:1"; // presents a certificate in the fake, refuses TCP in reality

    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public TrustOnFirstUseTests() => JsonStore.UseDirectory(_state);

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

    private static async Task SettleAsync(ConnectPageViewModel connect)
    {
        // Discovery debounces 450 ms and then probes; wait for it to land rather than for a fixed time.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && (connect.Reach is HostReach.Checking or HostReach.Unknown || connect.IsDiscovering))
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_typed_https_address_learns_and_shows_the_hosts_certificate()
    {
        var shell = NewShell();
        var probed = 0;
        var connect = new ConnectPageViewModel(shell, shell.Hosts, shell.Sessions,
            fingerprintProbe: (_, _) => { probed++; return Task.FromResult(Pin); })
        {
            Address = Url,
        };

        await SettleAsync(connect);

        Assert.Equal(1, probed);
        Assert.Equal(Pin, connect.TrustedFingerprint);
        Assert.StartsWith("01f9d489 13f5a642", connect.CertificateFingerprint, StringComparison.Ordinal);
        Assert.False(connect.CertificateChanged);
        // The pin is not the blocker any more; what is left is that nothing listens on port 1, and the
        // screen says that rather than anything about certificates.
        Assert.True(connect.IsUnreachable);
        Assert.DoesNotContain("certificate", connect.ReachText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_host_already_pinned_that_presents_a_different_key_is_refused()
    {
        var shell = NewShell();
        shell.Hosts.Add(new SavedHost("box", Url, "token", Other));
        var connect = new ConnectPageViewModel(shell, shell.Hosts, shell.Sessions,
            fingerprintProbe: (_, _) => Task.FromResult(Pin))
        {
            Address = Url,
        };

        await SettleAsync(connect);

        Assert.True(connect.CertificateChanged);
        Assert.True(connect.IsUnreachable);
        Assert.Contains("changed", connect.ReachText, StringComparison.OrdinalIgnoreCase);
        Assert.False(connect.CanSignIn);
        Assert.Null(connect.TrustedFingerprint);
    }

    [Fact]
    public async Task A_scanned_fingerprint_is_kept_and_never_re_learned()
    {
        var shell = NewShell();
        var probed = 0;
        var connect = new ConnectPageViewModel(shell, shell.Hosts, shell.Sessions, prefillUrl: Url, fingerprint: Pin,
            fingerprintProbe: (_, _) => { probed++; return Task.FromResult(Other); });

        await SettleAsync(connect);

        Assert.Equal(0, probed);
        Assert.Equal(Pin, connect.TrustedFingerprint);
        Assert.False(connect.HasCertificateNote);
    }

    [Fact]
    public async Task A_host_that_presents_no_certificate_falls_through_to_the_ordinary_probe()
    {
        var shell = NewShell();
        var connect = new ConnectPageViewModel(shell, shell.Hosts, shell.Sessions,
            fingerprintProbe: (_, _) => throw new InvalidOperationException("nothing there"))
        {
            Address = Url,
        };

        await SettleAsync(connect);

        Assert.Null(connect.TrustedFingerprint);
        Assert.False(connect.HasCertificateNote);
        Assert.True(connect.IsUnreachable);
    }
}
