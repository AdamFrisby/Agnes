using System.Linq;
using Agnes.Abstractions;
using Agnes.Protocol;
using Agnes.Ui.Core.Onboarding;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The client groups a host's advertised sign-in methods into distinct UX buckets by <see cref="AuthFlowKind"/>
/// (add this device / restore access / authorize a headless process) rather than one flat list, using the
/// per-method kind the host reports in <see cref="AuthMethods.Flows"/> and falling back to a per-method
/// default for hosts that predate it (connectivity/04 AC1).
/// </summary>
public class AuthMethodBucketsTests
{
    private static InMemoryOnboardingStore StoreAt(WizardStep step, AuthMethods methods)
        => new(new OnboardingState(WizardStep: step, PendingHostUrl: "https://h.test", PendingMethods: methods));

    private static SetupWizardViewModel Wizard(AuthMethods methods)
        => new(StoreAt(WizardStep.ChooseMethod, methods), (_, _) => Task.FromResult(methods), () => false);

    [Fact]
    public void A_mixed_set_is_grouped_into_the_right_flow_buckets()
    {
        var methods = new AuthMethods(
            Pairing: true, GitHub: true, GitHubClientId: "cid", Keypair: true,
            Flows:
            [
                new AuthMethodDescriptor("pairing", "Pairing code", AuthFlowKind.NewDevice),
                new AuthMethodDescriptor("github", "GitHub", AuthFlowKind.NewDevice),
                new AuthMethodDescriptor("keypair", "Keypair", AuthFlowKind.ConnectTerminal),
            ]);

        var groups = Wizard(methods).MethodGroups;

        var newDevice = groups.Single(g => g.Kind == AuthFlowKind.NewDevice);
        Assert.Equal(new[] { AuthMethodKind.Pairing, AuthMethodKind.GitHub }, newDevice.Methods.Select(m => m.Kind));

        var headless = groups.Single(g => g.Kind == AuthFlowKind.ConnectTerminal);
        Assert.Equal(AuthMethodKind.Keypair, Assert.Single(headless.Methods).Kind);

        // Buckets carry a human heading and are ordered new-device first, headless last.
        Assert.Equal(new[] { AuthFlowKind.NewDevice, AuthFlowKind.ConnectTerminal }, groups.Select(g => g.Kind));
        Assert.False(string.IsNullOrWhiteSpace(newDevice.Heading));
    }

    [Fact]
    public void A_reported_restore_kind_lands_in_its_own_bucket()
    {
        var methods = new AuthMethods(
            Pairing: true, GitHub: true, GitHubClientId: "cid", Keypair: false,
            Flows:
            [
                new AuthMethodDescriptor("pairing", "Pairing code", AuthFlowKind.NewDevice),
                new AuthMethodDescriptor("github", "GitHub", AuthFlowKind.RestoreAccount),
            ]);

        var groups = Wizard(methods).MethodGroups;

        Assert.Equal(AuthMethodKind.Pairing, Assert.Single(groups.Single(g => g.Kind == AuthFlowKind.NewDevice).Methods).Kind);
        Assert.Equal(AuthMethodKind.GitHub, Assert.Single(groups.Single(g => g.Kind == AuthFlowKind.RestoreAccount).Methods).Kind);
    }

    [Fact]
    public void A_legacy_host_without_flows_still_buckets_by_per_method_default()
    {
        // No Flows reported (older host): keypair defaults to the headless bucket, the rest to add-a-device.
        var methods = new AuthMethods(Pairing: true, GitHub: false, GitHubClientId: null, Keypair: true);

        var groups = Wizard(methods).MethodGroups;

        Assert.Equal(AuthMethodKind.Pairing, Assert.Single(groups.Single(g => g.Kind == AuthFlowKind.NewDevice).Methods).Kind);
        Assert.Equal(AuthMethodKind.Keypair, Assert.Single(groups.Single(g => g.Kind == AuthFlowKind.ConnectTerminal).Methods).Kind);
    }

    [Fact]
    public void Group_is_a_pure_function_over_options_and_omits_empty_buckets()
    {
        var options = new[]
        {
            new WizardAuthOption(AuthMethodKind.Pairing, "P", "", Flow: AuthFlowKind.NewDevice),
            new WizardAuthOption(AuthMethodKind.Keypair, "K", "", Flow: AuthFlowKind.ConnectTerminal),
        };

        var groups = AuthMethodBuckets.Group(options, o => o.Flow);

        Assert.Equal(2, groups.Count);
        Assert.DoesNotContain(groups, g => g.Kind == AuthFlowKind.RestoreAccount);
    }
}
