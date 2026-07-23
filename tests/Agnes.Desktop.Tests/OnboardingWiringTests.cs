using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Onboarding;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>Desktop glue for the onboarding feature: verifies MainWindowViewModel shows the setup wizard on a
/// fresh install, suppresses it once a host is paired, and keeps the showcase manually reachable.</summary>
public class OnboardingWiringTests
{
    private static (string Tabs, string Hosts) TempPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agnes-onboard-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "tabs.json"), Path.Combine(dir, "hosts.json"));
    }

    private static MainWindowViewModel NewVm(string tabsPath, string hostsPath, IOnboardingStore onboarding)
        => new(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(tabsPath), new HostRegistryStore(hostsPath), onboarding: onboarding);

    [Fact]
    public async Task Fresh_install_opens_the_setup_wizard_and_not_the_showcase()
    {
        var (tabs, hosts) = TempPaths();
        var vm = NewVm(tabs, hosts, new InMemoryOnboardingStore());

        await vm.RestoreAsync();

        Assert.True(vm.IsSetupWizardOpen);
        Assert.False(vm.Showcase.IsOpen); // the wizard takes precedence on a fresh install
    }

    [Fact]
    public async Task A_paired_host_suppresses_the_wizard_and_auto_shows_the_showcase_once()
    {
        var (tabs, hosts) = TempPaths();
        new HostRegistryStore(hosts).Save([new KnownHost("My host", "https://host.example:5099", "tok")]);
        var vm = NewVm(tabs, hosts, new InMemoryOnboardingStore());

        await vm.RestoreAsync();

        Assert.False(vm.IsSetupWizardOpen);
        Assert.True(vm.Showcase.IsOpen); // no wizard needed, so the feature tour runs on first launch
    }

    [Fact]
    public async Task Dismissed_showcase_does_not_reappear_but_stays_manually_reachable()
    {
        var (tabs, hosts) = TempPaths();
        new HostRegistryStore(hosts).Save([new KnownHost("My host", "https://host.example:5099", "tok")]);
        var store = new InMemoryOnboardingStore();

        var first = NewVm(tabs, hosts, store);
        await first.RestoreAsync();
        first.Showcase.Dismiss();

        // Relaunch with the same onboarding store: it must not auto-open again.
        var second = NewVm(tabs, hosts, store);
        await second.RestoreAsync();
        Assert.False(second.Showcase.IsOpen);

        // ...but the help/about command still opens it.
        second.ShowOnboardingCommand.Execute(null);
        Assert.True(second.Showcase.IsOpen);
    }

    [Fact]
    public async Task Choosing_a_wizard_method_opens_a_prefilled_add_host_tab()
    {
        var (tabs, hosts) = TempPaths();
        var vm = NewVm(tabs, hosts, new InMemoryOnboardingStore());
        await vm.RestoreAsync();

        vm.SetupWizard.HostUrl = "https://host.example:5099";
        vm.SetupWizard.HostName = "My host";
        await vm.SetupWizard.DiscoverMethodsAsync();
        await vm.SetupWizard.ChooseMethodAsync(vm.SetupWizard.Methods.First());

        Assert.False(vm.IsSetupWizardOpen);
        var addHostTab = DocumentDock(vm).VisibleDockables!.OfType<SessionDocument>()
            .First(d => d.ShowAddHost);
        Assert.Equal("https://host.example:5099", addHostTab.NewHostUrl);
        Assert.Equal("My host", addHostTab.NewHostName);
    }

    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];
}
