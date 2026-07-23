using Agnes.Protocol;
using Agnes.Ui.Core.Onboarding;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public class OnboardingTests
{
    private static SetupWizardViewModel Wizard(
        AuthMethods methods,
        IOnboardingStore? store = null,
        bool anyHostPaired = false)
        => new(
            store ?? new InMemoryOnboardingStore(),
            (_, _) => Task.FromResult(methods),
            () => anyHostPaired);

    // ---- wizard presents exactly the methods a host returned ----

    [Fact]
    public async Task Wizard_lists_only_the_methods_the_host_returned()
    {
        var methods = new AuthMethods(Pairing: true, GitHub: false, GitHubClientId: null, Keypair: true);
        var vm = Wizard(methods);
        vm.HostUrl = "https://host.example:5099";

        await vm.DiscoverMethodsAsync();

        Assert.Equal(WizardStep.ChooseMethod, vm.CurrentStep);
        Assert.Equal(
            [AuthMethodKind.Pairing, AuthMethodKind.Keypair],
            vm.Methods.Select(m => m.Kind));
        Assert.DoesNotContain(vm.Methods, m => m.Kind == AuthMethodKind.GitHub);
    }

    [Fact]
    public async Task Wizard_shows_github_when_enabled_and_omits_it_when_disabled()
    {
        var withGitHub = Wizard(new AuthMethods(true, GitHub: true, "client-id", Keypair: false));
        withGitHub.HostUrl = "https://host.example:5099";
        await withGitHub.DiscoverMethodsAsync();
        Assert.Contains(withGitHub.Methods, m => m.Kind == AuthMethodKind.GitHub);

        var withoutGitHub = Wizard(new AuthMethods(true, GitHub: false, null, Keypair: false));
        withoutGitHub.HostUrl = "https://host.example:5099";
        await withoutGitHub.DiscoverMethodsAsync();
        Assert.DoesNotContain(withoutGitHub.Methods, m => m.Kind == AuthMethodKind.GitHub);
    }

    [Fact]
    public async Task Wizard_surfaces_enterprise_methods_when_the_host_advertises_them()
    {
        var methods = new AuthMethods(
            Pairing: false, GitHub: false, GitHubClientId: null, Keypair: false,
            Oidc: true, OidcIssuer: "https://id.corp", Mtls: true);
        var vm = Wizard(methods);
        vm.HostUrl = "https://host.example:5099";

        await vm.DiscoverMethodsAsync();

        Assert.Equal(
            [AuthMethodKind.Oidc, AuthMethodKind.Mtls],
            vm.Methods.Select(m => m.Kind));
        Assert.Contains("id.corp", vm.Methods.First(m => m.Kind == AuthMethodKind.Oidc).Description);
    }

    // ---- "already paired" suppression ----

    [Fact]
    public void Wizard_should_show_when_no_host_is_paired()
    {
        var vm = Wizard(new AuthMethods(true, false, null, false), anyHostPaired: false);
        Assert.True(vm.ShouldShow);
    }

    [Fact]
    public void Wizard_should_not_show_once_a_host_is_paired()
    {
        var vm = Wizard(new AuthMethods(true, false, null, false), anyHostPaired: true);
        Assert.False(vm.ShouldShow);
    }

    // ---- resumable wizard ----

    [Fact]
    public async Task Cancelled_wizard_restores_its_step_and_host_from_persisted_state()
    {
        var store = new InMemoryOnboardingStore();
        var methods = new AuthMethods(true, GitHub: true, "client-id", Keypair: false);

        var first = Wizard(methods, store);
        first.HostUrl = "https://host.example:5099";
        first.HostName = "My host";
        await first.DiscoverMethodsAsync();   // persists ChooseMethod + host + methods
        first.Cancel();                        // dismiss without completing

        // A freshly constructed wizard (as on relaunch) reads the same store.
        var resumed = Wizard(methods, store);
        Assert.Equal(WizardStep.ChooseMethod, resumed.CurrentStep);
        Assert.Equal("https://host.example:5099", resumed.HostUrl);
        Assert.Equal("My host", resumed.HostName);
        Assert.Contains(resumed.Methods, m => m.Kind == AuthMethodKind.GitHub);
    }

    [Fact]
    public async Task Restart_clears_persisted_progress_back_to_the_host_step()
    {
        var store = new InMemoryOnboardingStore();
        var vm = Wizard(new AuthMethods(true, false, null, false), store);
        vm.HostUrl = "https://host.example:5099";
        await vm.DiscoverMethodsAsync();

        vm.Restart();

        Assert.Equal(WizardStep.EnterHost, vm.CurrentStep);
        Assert.Empty(vm.Methods);
        var persisted = store.Load();
        Assert.Equal(WizardStep.EnterHost, persisted.WizardStep);
        Assert.Null(persisted.PendingHostUrl);
    }

    [Fact]
    public async Task Completing_the_wizard_marks_it_done_and_clears_pending_progress()
    {
        var store = new InMemoryOnboardingStore();
        var vm = Wizard(new AuthMethods(true, false, null, false), store);
        vm.HostUrl = "https://host.example:5099";
        await vm.DiscoverMethodsAsync();

        vm.Complete();

        var persisted = store.Load();
        Assert.True(persisted.WizardCompleted);
        Assert.Equal(WizardStep.Completed, persisted.WizardStep);
        Assert.Null(persisted.PendingMethods);
    }

    [Fact]
    public async Task Choosing_a_method_without_a_runner_raises_the_choice_for_the_shell()
    {
        var vm = Wizard(new AuthMethods(true, GitHub: true, "id", false));
        vm.HostUrl = "https://host.example:5099";
        await vm.DiscoverMethodsAsync();

        AuthMethodKind? chosen = null;
        vm.MethodChosen += k => chosen = k;
        await vm.ChooseMethodAsync(vm.Methods.First(m => m.Kind == AuthMethodKind.GitHub));

        Assert.Equal(AuthMethodKind.GitHub, chosen);
        Assert.Equal(AuthMethodKind.GitHub, vm.SelectedMethod);
    }

    // ---- showcase: data-driven cards ----

    [Fact]
    public void Showcase_yields_one_step_per_card_with_titles_from_the_data_list()
    {
        var cards = new[]
        {
            new FeatureCard("Alpha", "first"),
            new FeatureCard("Beta", "second"),
            new FeatureCard("Gamma", "third"),
        };
        var vm = new ShowcaseViewModel(cards, new InMemoryOnboardingStore(), "1.0");
        vm.Show();

        Assert.Equal(3, vm.StepCount);
        Assert.Equal("Alpha", vm.Current!.Title);
        Assert.Equal("1 of 3", vm.StepLabel);

        vm.NextCommand.Execute(null);
        Assert.Equal("Beta", vm.Current!.Title);
        vm.NextCommand.Execute(null);
        Assert.Equal("Gamma", vm.Current!.Title);
        Assert.True(vm.IsLast);
        Assert.False(vm.HasNext);

        vm.PrevCommand.Execute(null);
        Assert.Equal("Beta", vm.Current!.Title);
    }

    [Fact]
    public void Default_showcase_cards_are_a_nonempty_data_list()
    {
        var vm = new ShowcaseViewModel(OnboardingCards.Default, new InMemoryOnboardingStore(), "1.0");
        Assert.True(vm.StepCount >= 5);
        Assert.All(vm.Cards, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));
    }

    // ---- showcase: shown-once ----

    [Fact]
    public void Showcase_auto_shows_on_a_fresh_install()
    {
        var vm = new ShowcaseViewModel(OnboardingCards.Default, new InMemoryOnboardingStore(), "2.0");
        Assert.True(vm.ShouldAutoShow);
    }

    [Fact]
    public void Showcase_does_not_auto_show_after_dismissal_but_stays_reachable_manually()
    {
        var store = new InMemoryOnboardingStore();
        var vm = new ShowcaseViewModel(OnboardingCards.Default, store, "2.0");
        vm.Show();
        vm.Dismiss();

        // A relaunch: a fresh VM over the same store must not auto-show this version...
        var relaunched = new ShowcaseViewModel(OnboardingCards.Default, store, "2.0");
        Assert.False(relaunched.ShouldAutoShow);

        // ...but is still openable manually (help/about entry).
        relaunched.Show();
        Assert.True(relaunched.IsOpen);
    }

    [Fact]
    public void A_new_app_version_auto_shows_the_showcase_again()
    {
        var store = new InMemoryOnboardingStore();
        new ShowcaseViewModel(OnboardingCards.Default, store, "2.0").Dismiss();

        var newVersion = new ShowcaseViewModel(OnboardingCards.Default, store, "3.0");
        Assert.True(newVersion.ShouldAutoShow);
    }

    // ---- file-backed store round-trip (temp dir) ----

    [Fact]
    public void File_store_round_trips_state_through_a_temp_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "agnes-onboard-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileOnboardingStore(path);
            store.Save(new OnboardingState(
                WizardCompleted: true,
                ShowcaseShownVersion: "9.9",
                WizardStep: WizardStep.ChooseMethod,
                PendingHostUrl: "https://host.example:5099",
                PendingMethods: new AuthMethods(true, true, "cid", false)));

            var reloaded = new FileOnboardingStore(path).Load();
            Assert.True(reloaded.WizardCompleted);
            Assert.Equal("9.9", reloaded.ShowcaseShownVersion);
            Assert.Equal(WizardStep.ChooseMethod, reloaded.WizardStep);
            Assert.Equal("https://host.example:5099", reloaded.PendingHostUrl);
            Assert.NotNull(reloaded.PendingMethods);
            Assert.True(reloaded.PendingMethods!.GitHub);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
