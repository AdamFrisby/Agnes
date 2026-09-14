using Agnes.Ui.Core;
using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Mobile.Tests;

/// <summary>
/// Setting CodeyBox up, and — much more importantly — what the app looks like for the overwhelming
/// majority of devices that never will.
/// </summary>
/// <remarks>
/// The gate is the feature's first requirement: almost nobody running Agnes runs CodeyBox, and for them
/// the app has to be exactly the four-destination app docs/mobile.md describes. Not "a tab that says
/// nothing" — no tab, no inbox section, and no requests at all.
/// </remarks>
[Collection(AvaloniaCollection.Name)]
public sealed class CodeyBoxSetupTests : IDisposable
{
    private readonly AvaloniaSession _avalonia;

    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "agnes-codeybox-setup-" + Guid.NewGuid().ToString("n"));

    public CodeyBoxSetupTests(AvaloniaSession avalonia)
    {
        _avalonia = avalonia;
        JsonStore.UseDirectory(_state);
    }

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

    [Fact]
    public void A_url_without_a_key_is_not_configured()
    {
        // A URL with no key reaches an orchestrator that answers 401 to everything, which on screen is
        // indistinguishable from a broken one. Half-configured is not configured.
        Assert.False(new CodeyBoxConfig("http://10.0.0.188:5836").IsConfigured);
        Assert.False(new CodeyBoxConfig(ApiKey: "k").IsConfigured);
        Assert.True(new CodeyBoxConfig("http://10.0.0.188:5836", "k").IsConfigured);
    }

    [Theory]
    // The operator's own machine, wherever it is: loopback, the three RFC1918 blocks, link-local, and
    // the CGNAT range a tailnet lives in.
    [InlineData("http://127.0.0.1:5836", true)]
    [InlineData("http://localhost:5836", true)]
    [InlineData("http://10.0.0.188:5836", true)]
    [InlineData("http://172.16.4.2:5836", true)]
    [InlineData("http://172.32.4.2:5836", false)]
    [InlineData("http://192.168.1.10:5836", true)]
    [InlineData("http://169.254.7.7:5836", true)]
    [InlineData("http://100.105.1.30:5836", true)]
    [InlineData("http://[fd12::1]:5836", true)]
    // A bearer key does not cross the internet in the clear on our account.
    [InlineData("http://codeybox.example.com:5836", false)]
    [InlineData("http://93.184.216.34:5836", false)]
    // TLS goes through the platform stack with its trust anchors, exactly like an Agnes host.
    [InlineData("https://10.0.0.188:5836", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void Cleartext_is_allowed_only_to_somewhere_this_network_can_reach(string url, bool allowed)
    {
        // The app bans cleartext app-wide (Resources/xml/network_security_config.xml) and Android cannot
        // express "except to a private address" — its <domain> entries are hostnames, not CIDRs. So this
        // predicate is where that exception lives, and AndroidCodeyBoxTransport is the only thing that
        // acts on it. Without it, a plain-http CodeyBox on the LAN fails before any of our code runs,
        // with an error indistinguishable from an unreachable address.
        Assert.Equal(allowed, CodeyBoxConfig.IsPrivateCleartext(url));
    }

    [Fact]
    public void An_unreachable_address_and_a_refused_key_are_told_apart()
    {
        // The two failures that actually happen on a phone, and a raw HttpRequestException says neither.
        Assert.Contains(
            "same network",
            CodeyBoxViewModel.Explain(new HttpRequestException("nope")),
            StringComparison.Ordinal);
        Assert.Contains(
            "API key",
            CodeyBoxViewModel.Explain(new HttpRequestException(null, null, System.Net.HttpStatusCode.Unauthorized)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_trims_what_was_typed_and_survives_a_relaunch()
    {
        var (shell, fleet) = Fleet.Offline();
        fleet.Save(new CodeyBoxConfig());

        var page = new CodeyBoxSetupPageViewModel(shell, fleet)
        {
            Address = "http://10.0.0.188:5836/",
            ApiKey = " secret ",
        };

        Assert.True(page.CanSave);
        page.SaveCommand.Execute(null);

        // A trailing slash doubles up against the client's own, and a key pasted out of a terminal
        // usually brings whitespace with it.
        Assert.Equal("http://10.0.0.188:5836", fleet.Config.NormalizedUrl);
        Assert.Equal("secret", fleet.Config.ApiKey);
        Assert.True(CodeyBoxConfig.Load().IsConfigured);
        Assert.Equal(1, shell.Pops);

        new CodeyBoxSetupPageViewModel(shell, fleet).ForgetCommand.Execute(null);
        Assert.False(fleet.IsConfigured);
        Assert.False(CodeyBoxConfig.Load().IsConfigured);
    }

    [Fact]
    public async Task Test_says_what_it_found()
    {
        var handler = new FleetHandler();
        var (shell, fleet) = Fleet.Offline(handler);
        var page = new CodeyBoxSetupPageViewModel(shell, fleet)
        {
            Address = "http://10.0.0.188:5836",
            ApiKey = "k",
        };

        // The real client is built from the TYPED values, so this goes out — it just goes to the fake.
        // The route is the cheapest thing the orchestrator serves and it touches nothing.
        await page.TestCommand.ExecuteAsync(null);

        Assert.Contains("Found CodeyBox", page.Status, StringComparison.Ordinal);
        Assert.Contains("running", page.Status, StringComparison.Ordinal);
        Assert.Equal(["GET /queue/status"], handler.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Fact]
    public async Task An_unconfigured_device_has_no_fleet_at_all()
    {
        await _avalonia.Run(() =>
        {
            new CodeyBoxConfig().Save();
            var shell = new ShellViewModel(
                new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "No fleet");

            Assert.False(shell.HasCodeyBox);
            Assert.False(shell.CodeyBox.IsConfigured);
            Assert.Empty(shell.Inbox.CodeyBoxRows);
            Assert.False(shell.Inbox.HasCodeyBoxRows);
        });
    }

    [Fact]
    public async Task Forgetting_the_fleet_while_standing_on_its_tab_puts_you_back_on_sessions()
    {
        // Otherwise the app is left showing a destination that no longer exists.
        await _avalonia.Run(() =>
        {
            var shell = Fleet.Shell();
            Assert.True(shell.HasCodeyBox);

            shell.SelectTab(ShellTab.CodeyBox);
            Assert.Equal(ShellTab.CodeyBox, shell.Tab);

            shell.CodeyBox.Save(new CodeyBoxConfig());

            Assert.False(shell.HasCodeyBox);
            Assert.Equal(ShellTab.Sessions, shell.Tab);
        });
    }

    [Fact]
    public async Task A_configured_fleet_that_is_left_stops_following()
    {
        await _avalonia.Run(() =>
        {
            // A handler, because "live" means a client to follow: without one there is nothing to
            // connect to and OnShown has nothing to start.
            var shell = Fleet.Shell(new FleetHandler());
            shell.SelectTab(ShellTab.CodeyBox);
            Assert.True(shell.CodeyBox.IsLive);

            // A phone in a pocket must not hold a connection open to something nobody is reading.
            shell.SelectTab(ShellTab.Sessions);
            Assert.False(shell.CodeyBox.IsLive);
            Assert.False(shell.CodeyBox.NowWorking.IsRunning);
        });
    }

    [Fact]
    public async Task The_wall_is_calm_on_a_phone_until_asked_otherwise()
    {
        // Calm keeps every live update and removes every flash, pulse and count-up. On a 33ms frame timer
        // that is the difference between a screen you can leave open and one that warms the phone.
        await _avalonia.Run(() =>
        {
            var shell = Fleet.Shell(new FleetHandler());
            Assert.True(shell.CodeyBox.NowWorking.IsCalm);

            shell.SelectTab(ShellTab.CodeyBox);
            shell.CodeyBox.ShowNowWorkingCommand.Execute(null);
            Assert.True(shell.CodeyBox.NowWorking.IsRunning);
            Assert.False(shell.CodeyBox.NowWorking.IsAnimating);

            shell.CodeyBox.NowWorking.ToggleCalmCommand.Execute(null);
            Assert.True(shell.CodeyBox.NowWorking.IsAnimating);

            shell.SelectTab(ShellTab.Sessions);
        });
    }
}

/// <summary>
/// The way a phone should reach an orchestrator that listens on loopback beside its host: through the
/// host's bounded fleet proxy, with the phone's own pairing and the host's certificate pin, and no
/// orchestrator key on the phone at all.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class CodeyBoxViaHostTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-codeybox-via-" + Guid.NewGuid().ToString("n"));

    public CodeyBoxViaHostTests(AvaloniaSession avalonia)
    {
        _ = avalonia;
        JsonStore.UseDirectory(_state);
    }

    public void Dispose()
    {
        try { Directory.Delete(_state, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void A_fleet_through_a_host_borrows_that_hosts_pairing_and_pin()
    {
        var hosts = new HostBook(new MobileConnector(), ImmediateDispatcher.Instance);
        hosts.Add(new SavedHost("Workshop", "https://10.0.0.188:5081", "device-token", "01f9d489aa"));

        var config = new CodeyBoxConfig(HostUrl: "https://10.0.0.188:5081");
        Assert.True(config.ViaHost);
        Assert.True(config.IsConfigured);

        var endpoint = config.Resolve(hosts);
        Assert.NotNull(endpoint);
        Assert.Equal("https://10.0.0.188:5081/fleet", endpoint!.Options.BaseUrl);
        Assert.Equal("device-token", endpoint.Options.ApiKey);
        Assert.Equal("01f9d489aa", endpoint.Fingerprint);

        // A host that is no longer paired is no way in; the tab stays gated rather than pointing at nothing.
        Assert.Null(new CodeyBoxConfig(HostUrl: "https://gone.example").Resolve(hosts));

        // Direct stays what it was, with nothing to pin.
        var direct = new CodeyBoxConfig("http://10.0.0.188:5836", "k").Resolve(hosts);
        Assert.Equal("http://10.0.0.188:5836", direct!.Options.BaseUrl);
        Assert.Null(direct.Fingerprint);
    }

    [Fact]
    public async Task The_setup_page_offers_each_paired_host_and_says_whether_it_has_a_fleet()
    {
        var shell = new StubShell();
        shell.Hosts.Add(DemoHost.Saved);
        var fleet = new CodeyBoxViewModel(shell, clientFactory: _ => null);

        var page = new CodeyBoxSetupPageViewModel(shell, fleet);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (page.Hosts.Count == 0)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }

        var choice = Assert.Single(page.Hosts);
        Assert.Equal(DemoHost.Saved.Name, choice.Name);
        // The demo host advertises no fleet, so its row says so and cannot be chosen.
        Assert.False(choice.OffersFleet);
        Assert.Equal("No fleet configured on this host", choice.Detail);
        Assert.False(page.IsViaHost);
    }
}
