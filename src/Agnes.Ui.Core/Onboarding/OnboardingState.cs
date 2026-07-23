using Agnes.Protocol;

namespace Agnes.Ui.Core.Onboarding;

/// <summary>The bootstrap auth methods the setup wizard can sequence, mirroring <see cref="AuthMethods"/>.
/// Each corresponds to an existing client flow — the wizard chooses which to offer, it does not add new ones.</summary>
public enum AuthMethodKind
{
    Pairing,
    GitHub,
    Keypair,
    Oidc,
    Mtls,
}

/// <summary>Where a partway-through setup wizard left off, so a cancelled run resumes cleanly rather than
/// restarting from a broken intermediate state.</summary>
public enum WizardStep
{
    /// <summary>Entering the host address (the starting step).</summary>
    EnterHost,

    /// <summary>A host was reached and its <see cref="AuthMethods"/> discovered — pick a sign-in method.</summary>
    ChooseMethod,

    /// <summary>Pairing completed; the wizard is done.</summary>
    Completed,
}

/// <summary>
/// Client-local, persisted onboarding progress: whether the first-run setup wizard finished, which app
/// version's showcase has already been shown (so it shows once per install and can later drive a "what's new"
/// surface), and enough wizard state (<see cref="WizardStep"/> + the pending host and its discovered methods)
/// to resume a cancelled wizard. Immutable — mutate via <c>with</c> and persist through <see cref="IOnboardingStore"/>.
/// </summary>
public sealed record OnboardingState(
    bool WizardCompleted = false,
    string? ShowcaseShownVersion = null,
    WizardStep WizardStep = WizardStep.EnterHost,
    string? PendingHostUrl = null,
    string? PendingHostName = null,
    AuthMethods? PendingMethods = null);
