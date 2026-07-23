using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Protocol;
using Agnes.Ui.Core.Onboarding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>One sign-in method the wizard offers, derived from a host's <see cref="AuthMethods"/>. <see cref="DocsUrl"/>
/// links out to <c>docs/deployment.md</c> for the host-side setup a method may need (e.g. a GitHub OAuth app),
/// rather than duplicating that content in the client.</summary>
public sealed record WizardAuthOption(AuthMethodKind Kind, string Label, string Description, string? DocsUrl = null);

/// <summary>
/// Drives the first-run setup wizard: a thin, resumable sequence over the client's <em>existing</em>
/// pairing/auth flows. It (1) takes a host address, (2) asks the host <c>GET /auth/methods</c> via the injected
/// <c>fetchMethods</c> delegate, (3) offers <em>only</em> the methods that host actually returned, and (4) runs
/// the chosen flow through the injected <c>runFlow</c> delegate — the desktop wires that to
/// <see cref="Agnes.Client"/>'s real pairing/GitHub/keypair calls, so nothing here reimplements pairing.
/// <para>
/// It does not show once a host is already paired (<see cref="ShouldShow"/> consults the injected
/// <c>anyHostPaired</c>). Progress is persisted at each step, so a cancelled wizard resumes from where it left
/// off (<see cref="WizardStep"/> + the pending host and its discovered methods) or restarts cleanly.
/// </para>
/// </summary>
public sealed class SetupWizardViewModel : ObservableObject
{
    /// <summary>Where host-side setup (registering a GitHub OAuth app, TLS, keypairs) is documented — the wizard
    /// links out to this rather than duplicating it.</summary>
    public const string DeploymentDocsUrl = "https://github.com/AdamFrisby/Agnes/blob/main/docs/deployment.md";

    private readonly IOnboardingStore _store;
    private readonly Func<string, CancellationToken, Task<AuthMethods>> _fetchMethods;
    private readonly Func<bool> _anyHostPaired;
    private readonly Func<SetupWizardViewModel, AuthMethodKind, CancellationToken, Task<bool>>? _runFlow;

    public SetupWizardViewModel(
        IOnboardingStore store,
        Func<string, CancellationToken, Task<AuthMethods>> fetchMethods,
        Func<bool> anyHostPaired,
        Func<SetupWizardViewModel, AuthMethodKind, CancellationToken, Task<bool>>? runFlow = null)
    {
        _store = store;
        _fetchMethods = fetchMethods;
        _anyHostPaired = anyHostPaired;
        _runFlow = runFlow;

        DiscoverCommand = new AsyncRelayCommand(DiscoverMethodsAsync, () => CanDiscover);
        ChooseMethodCommand = new AsyncRelayCommand<WizardAuthOption>(ChooseMethodAsync);
        BackCommand = new RelayCommand(Restart);
        RestartCommand = new RelayCommand(Restart);
        CancelCommand = new RelayCommand(Cancel);

        // Restore any partway-through progress so a cancelled wizard resumes cleanly.
        var state = _store.Load();
        _hostUrl = state.PendingHostUrl ?? string.Empty;
        _hostName = state.PendingHostName ?? string.Empty;
        _step = state.WizardStep;
        if (state.WizardStep == WizardStep.ChooseMethod && state.PendingMethods is not null)
        {
            BuildMethods(state.PendingMethods);
        }
    }

    /// <summary>Whether the first-run wizard should appear: only when no host is paired yet and it hasn't been
    /// completed. Once any host is paired it stays hidden (the acceptance criterion for a returning user).</summary>
    public bool ShouldShow => !_anyHostPaired() && !_store.Load().WizardCompleted;

    private WizardStep _step;
    /// <summary>The current step, restored from persisted progress on construction.</summary>
    public WizardStep CurrentStep
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsEnteringHost));
                OnPropertyChanged(nameof(IsChoosingMethod));
                OnPropertyChanged(nameof(IsCompleted));
            }
        }
    }

    public bool IsEnteringHost => _step == WizardStep.EnterHost;
    public bool IsChoosingMethod => _step == WizardStep.ChooseMethod;
    public bool IsCompleted => _step == WizardStep.Completed;

    private string _hostUrl;
    /// <summary>The host address the user is connecting to.</summary>
    public string HostUrl
    {
        get => _hostUrl;
        set
        {
            if (SetProperty(ref _hostUrl, value))
            {
                (DiscoverCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    private string _hostName = string.Empty;
    /// <summary>An optional friendly name for the host (defaults to the URL when blank).</summary>
    public string HostName { get => _hostName; set => SetProperty(ref _hostName, value); }

    /// <summary>The pairing code (for the pairing-code flow) the desktop hands to <see cref="Agnes.Client"/>.</summary>
    private string _pairingCode = string.Empty;
    public string PairingCode { get => _pairingCode; set => SetProperty(ref _pairingCode, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (DiscoverCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    private string _status = string.Empty;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>The sign-in methods this host offers — populated from <c>GET /auth/methods</c>, so it lists
    /// exactly what the host returned and nothing more.</summary>
    public ObservableCollection<WizardAuthOption> Methods { get; } = [];

    private AuthMethodKind? _selectedMethod;
    /// <summary>The method the user picked to run (null until they choose one).</summary>
    public AuthMethodKind? SelectedMethod { get => _selectedMethod; private set => SetProperty(ref _selectedMethod, value); }

    public bool CanDiscover => !IsBusy && IsValidHostUrl(_hostUrl);

    public ICommand DiscoverCommand { get; }
    public ICommand ChooseMethodCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Raised when a method is chosen but no <c>runFlow</c> was supplied (pure sequencing / tests).</summary>
    public event Action<AuthMethodKind>? MethodChosen;

    /// <summary>Ask the host which methods it offers, then advance to the choose-a-method step. Persists the
    /// host and the discovered methods so the step survives a cancel/relaunch.</summary>
    public async Task DiscoverMethodsAsync(CancellationToken cancellationToken = default)
    {
        var url = _hostUrl.Trim();
        if (!IsValidHostUrl(url))
        {
            Status = "Enter a host address like https://your-host:5099";
            return;
        }

        IsBusy = true;
        Status = "Contacting the host…";
        try
        {
            var methods = await _fetchMethods(url, cancellationToken).ConfigureAwait(true);
            BuildMethods(methods);
            _store.Save(_store.Load() with
            {
                WizardStep = WizardStep.ChooseMethod,
                PendingHostUrl = url,
                PendingHostName = string.IsNullOrWhiteSpace(_hostName) ? null : _hostName.Trim(),
                PendingMethods = methods,
            });
            CurrentStep = WizardStep.ChooseMethod;
            Status = Methods.Count == 0
                ? "This host didn't offer a sign-in method the client recognises. See the deployment docs."
                : string.Empty;
        }
        catch (Exception ex)
        {
            Status = "Couldn't reach the host: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Run the chosen method's existing flow (via <c>runFlow</c>); on success mark the wizard complete.
    /// With no <c>runFlow</c> supplied it just records the choice and raises <see cref="MethodChosen"/>.</summary>
    public async Task ChooseMethodAsync(WizardAuthOption? option, CancellationToken cancellationToken = default)
    {
        if (option is null)
        {
            return;
        }

        SelectedMethod = option.Kind;
        if (_runFlow is null)
        {
            MethodChosen?.Invoke(option.Kind);
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _runFlow(this, option.Kind, cancellationToken).ConfigureAwait(true);
            if (ok)
            {
                Complete();
            }
        }
        catch (Exception ex)
        {
            Status = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Mark the wizard done (a host was paired). Clears the resumable progress.</summary>
    public void Complete()
    {
        _store.Save(_store.Load() with
        {
            WizardCompleted = true,
            WizardStep = WizardStep.Completed,
            PendingHostUrl = null,
            PendingHostName = null,
            PendingMethods = null,
        });
        CurrentStep = WizardStep.Completed;
    }

    /// <summary>Start over from the host-entry step, discarding partway progress.</summary>
    public void Restart()
    {
        Methods.Clear();
        SelectedMethod = null;
        Status = string.Empty;
        _store.Save(_store.Load() with
        {
            WizardStep = WizardStep.EnterHost,
            PendingHostUrl = null,
            PendingHostName = null,
            PendingMethods = null,
        });
        CurrentStep = WizardStep.EnterHost;
    }

    /// <summary>Dismiss the wizard without losing progress — it resumes from the persisted step next launch.</summary>
    public void Cancel() => Dismissed?.Invoke();

    /// <summary>Raised when the user cancels so the shell can hide the overlay (progress stays persisted).</summary>
    public event Action? Dismissed;

    private void BuildMethods(AuthMethods methods)
    {
        Methods.Clear();
        if (methods.Pairing)
        {
            Methods.Add(new WizardAuthOption(AuthMethodKind.Pairing, "Pairing code",
                "Enter the short code the host prints on startup.", DeploymentDocsUrl));
        }

        if (methods.GitHub)
        {
            Methods.Add(new WizardAuthOption(AuthMethodKind.GitHub, "Sign in with GitHub",
                "Authorise this device with your GitHub account.", DeploymentDocsUrl));
        }

        if (methods.Keypair)
        {
            Methods.Add(new WizardAuthOption(AuthMethodKind.Keypair, "Device key",
                "Use this device's key; add its public line to the host's authorized keys.", DeploymentDocsUrl));
        }

        if (methods.Oidc)
        {
            var hint = string.IsNullOrEmpty(methods.OidcIssuer)
                ? "Sign in through your organisation's identity provider."
                : $"Sign in through {methods.OidcIssuer}.";
            Methods.Add(new WizardAuthOption(AuthMethodKind.Oidc, "Single sign-on (OIDC)", hint, DeploymentDocsUrl));
        }

        if (methods.Mtls)
        {
            Methods.Add(new WizardAuthOption(AuthMethodKind.Mtls, "Client certificate (mTLS)",
                "Pair using the client certificate presented on the TLS connection.", DeploymentDocsUrl));
        }
    }

    private static bool IsValidHostUrl(string url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(u.Host);
}
