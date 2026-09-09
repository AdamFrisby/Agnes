using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>
/// Clicking an <c>agnes://</c> link: one window gets it, and it lands on the connect surface already filled
/// in. Covers the three parts that can be exercised without a desktop environment — the instance gate's
/// forwarding, the handler declaration written for the OS, and what the app does with a link once it has one.
/// </summary>
public class UrlActivationTests
{
    // ---- one window per machine ----

    [Fact]
    public async Task A_second_launch_is_turned_away_and_hands_its_link_to_the_first()
    {
        var key = $"agnes-test-{Guid.NewGuid():n}";
        using var first = SingleInstance.TryClaim(key, SingleInstance.ActivateOnly);
        Assert.NotNull(first);
        Assert.True(first.IsGuarded);

        var received = new TaskCompletionSource<string>();
        first.MessageReceived += m => received.TrySetResult(m);

        var second = SingleInstance.TryClaim(key, "agnes://pair?host=https%3A%2F%2Fbox%3A5099&grant=G");

        // The second launch does not become an instance...
        Assert.Null(second);
        // ...and what it was opened with arrives at the one that is.
        Assert.Equal("agnes://pair?host=https%3A%2F%2Fbox%3A5099&grant=G", await WithinTenSeconds(received.Task));
    }

    [Fact]
    public async Task A_plain_second_launch_just_asks_the_first_to_come_forward()
    {
        var key = $"agnes-test-{Guid.NewGuid():n}";
        using var first = SingleInstance.TryClaim(key, SingleInstance.ActivateOnly);
        var received = new TaskCompletionSource<string>();
        first!.MessageReceived += m => received.TrySetResult(m);

        Assert.Null(SingleInstance.TryClaim(key, SingleInstance.ActivateOnly));

        var message = await WithinTenSeconds(received.Task);
        Assert.Equal(SingleInstance.ActivateOnly, message);
        Assert.False(UriScheme.IsSchemeArgument(message), "an activate ping is not a link");
    }

    [Fact]
    public async Task A_link_arriving_the_instant_after_startup_is_not_lost()
    {
        // The listener has to exist by the time the claim is handed back, not shortly afterwards. It used to
        // be opened on a background task, so launching Agnes and clicking a link a moment later raced it and
        // the link silently did nothing — intermittently, which is the worst way for it to fail.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var key = $"agnes-test-{Guid.NewGuid():n}";
            using var first = SingleInstance.TryClaim(key, SingleInstance.ActivateOnly);
            var received = new TaskCompletionSource<string>();
            first!.MessageReceived += m => received.TrySetResult(m);

            // No delay whatsoever between claiming and sending.
            Assert.Null(SingleInstance.TryClaim(key, $"agnes://pair?host=https%3A%2F%2Fbox&grant=G{attempt}"));

            Assert.Equal($"agnes://pair?host=https%3A%2F%2Fbox&grant=G{attempt}", await WithinTenSeconds(received.Task));
        }
    }

    [Fact]
    public void Releasing_the_claim_lets_the_next_launch_become_the_instance()
    {
        var key = $"agnes-test-{Guid.NewGuid():n}";
        var first = SingleInstance.TryClaim(key, SingleInstance.ActivateOnly);
        Assert.NotNull(first);

        first.Dispose(); // what closing the app does

        using var next = SingleInstance.TryClaim(key, SingleInstance.ActivateOnly);
        Assert.NotNull(next);
        Assert.True(next.IsGuarded);
    }

    // ---- telling the OS we handle the scheme ----

    [Theory]
    [InlineData("agnes://pair?host=https://box:5099", true)]
    [InlineData("AGNES://pair?host=x", true)]
    [InlineData("https://example.com", false)]
    [InlineData("--some-flag", false)]
    [InlineData(null, false)]
    public void Only_an_agnes_link_counts_as_an_activation_argument(string? argument, bool expected)
        => Assert.Equal(expected, UriScheme.IsSchemeArgument(argument));

    [Fact]
    public void The_desktop_entry_claims_the_scheme_and_passes_the_url_through()
    {
        var entry = UriScheme.DesktopEntry("/opt/agnes/Agnes");

        Assert.Contains("MimeType=x-scheme-handler/agnes;", entry, StringComparison.Ordinal);
        // %u is what hands the clicked URL to the process; without it the app opens with no idea why.
        Assert.Contains("Exec=/opt/agnes/Agnes %u", entry, StringComparison.Ordinal);
        Assert.Contains("Type=Application", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void An_executable_path_with_spaces_is_quoted()
        => Assert.Contains("Exec=\"/home/a b/Agnes\" %u", UriScheme.DesktopEntry("/home/a b/Agnes"), StringComparison.Ordinal);

    // ---- what the app does with a link ----

    [Fact]
    public void A_scanned_grant_fills_the_connect_form_and_starts_pairing_itself()
    {
        var vm = NewVm();
        var link = PairingLink.Build("https://box:5099", grant: "GRANT-1", fingerprint: new string('a', 64));

        vm.HandleLink(link);

        // Auto-submit can finish quickly enough to hide this form while another blank connect tab is still
        // visible. Select the document created for the link instead of racing ShowAddHost.
        var tab = Tabs(vm).Single(d => d.NewHostUrl == "https://box:5099");
        Assert.Equal("https://box:5099", tab.NewHostUrl);
        Assert.Equal("GRANT-1", tab.NewHostToken);
        Assert.Equal(new string('a', 64), tab.HostFingerprint);
        // The grant came off the host's own screen, so there's nothing left to confirm: pairing is already
        // under way by the time the call returns (the status has moved on to the attempt itself).
        Assert.Contains("Pairing", tab.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_typed_code_is_prefilled_but_waits_for_the_person()
    {
        var vm = NewVm();

        vm.HandleLink("agnes://pair?host=https%3A%2F%2Fbox%3A5099&code=ABCD-EFGH");

        var tab = Tabs(vm).Single(d => d.ShowAddHost);
        Assert.Equal("ABCD-EFGH", tab.NewHostToken);
        // A typed code proves nothing about where the link came from, so nothing is submitted for you.
        Assert.DoesNotContain("Pairing", tab.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_reuses_an_unpaired_tab_rather_than_stacking_one_up_per_click()
    {
        var vm = NewVm();

        vm.HandleLink("agnes://pair?host=https%3A%2F%2Fbox%3A5099&code=A");
        var opened = Tabs(vm).Count();
        vm.HandleLink("agnes://pair?host=https%3A%2F%2Fother%3A5099&code=B");

        // The second link lands in the tab the first one opened, rather than adding another.
        Assert.Equal(opened, Tabs(vm).Count());
        Assert.Equal("https://other:5099", Tabs(vm).Single(d => d.ShowAddHost).NewHostUrl);
    }

    [Fact]
    public void A_link_with_no_usable_address_is_ignored_rather_than_opening_an_empty_form()
    {
        var vm = NewVm();

        vm.HandleLink("agnes://pair?grant=G");

        Assert.DoesNotContain(Tabs(vm), d => d.ShowAddHost);
    }

    /// <summary>Fails with a clear message rather than hanging the suite if a message never arrives.</summary>
    private static async Task<string> WithinTenSeconds(Task<string> message)
    {
        var completed = await Task.WhenAny(message, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(message, completed);
        return await message;
    }

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => ((IDocumentDock)vm.Layout.VisibleDockables![0]).VisibleDockables!.OfType<SessionDocument>();

    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-url-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-url-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-url-arch-{id}.json")));
    }
}
