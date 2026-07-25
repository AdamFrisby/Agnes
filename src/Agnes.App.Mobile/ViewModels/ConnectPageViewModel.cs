using Agnes.App.Mobile.Services;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// Pairing this phone with a host.
///
/// The flow is address-first, then whatever that host actually offers — the app asks
/// <c>GET /auth/methods</c> and only shows the methods that will work, rather than presenting three
/// sign-in buttons and letting two of them fail. Typing an address on a phone is miserable, so the
/// screen also accepts an <c>agnes://</c> link (scan the host's QR with the system camera and it
/// arrives here pre-filled).
/// </summary>
public sealed partial class ConnectPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;
    private readonly HostBook _hosts;
    private readonly SessionsViewModel _sessions;
    private CancellationTokenSource? _discovery;

    public ConnectPageViewModel(IAppShell shell, HostBook hosts, SessionsViewModel sessions, string? prefillUrl = null, string? prefillCode = null)
    {
        _shell = shell;
        _hosts = hosts;
        _sessions = sessions;
        _address = prefillUrl ?? "https://";
        _code = prefillCode ?? string.Empty;

        PairCommand = new AsyncRelayCommand(PairAsync, () => IsAddressUsable && Code.Trim().Length > 0);
        GitHubCommand = new AsyncRelayCommand(GitHubAsync, () => IsAddressUsable);
        KeyCommand = new AsyncRelayCommand(KeyAsync, () => IsAddressUsable);
        CopyKeyCommand = new RelayCommand(() => _shell.CopyToClipboard(PublicKeyLine, "Public key"));
        OpenVerificationCommand = new RelayCommand(() => _shell.OpenUrl(VerificationUri));
        CopyUserCodeCommand = new RelayCommand(() => _shell.CopyToClipboard(UserCode, "Code"));
        DocsCommand = new RelayCommand(() => _shell.OpenUrl(DocsUrl));

        if (prefillUrl is not null)
        {
            _ = DiscoverAsync();
        }
    }

    private const string DocsUrl = "https://github.com/AdamFrisby/Agnes/blob/main/docs/deployment.md";

    public override string Title => "Connect a host";


    // ---- address ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddressUsable))]
    private string _address;

    partial void OnAddressChanged(string value)
    {
        PairCommand.NotifyCanExecuteChanged();
        GitHubCommand.NotifyCanExecuteChanged();
        KeyCommand.NotifyCanExecuteChanged();
        _ = DiscoverAsync();
    }

    /// <summary>Whether the typed address is a plausible https host — Connect stays disabled until it is,
    /// so the first failure isn't a network round trip.</summary>
    public bool IsAddressUsable => IsValidUrl(Address.Trim());

    private static bool IsValidUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https"
           && !string.IsNullOrEmpty(uri.Host);

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _code = string.Empty;

    partial void OnCodeChanged(string value) => PairCommand.NotifyCanExecuteChanged();

    // ---- discovered methods ----

    [ObservableProperty]
    private bool _isDiscovering;

    [ObservableProperty]
    private bool _supportsPairing = true;

    [ObservableProperty]
    private bool _supportsGitHub;

    [ObservableProperty]
    private bool _supportsKeypair;

    private string? _gitHubClientId;

    /// <summary>Discovers which sign-in methods the entered host offers. Re-run on every keystroke, with
    /// the previous probe cancelled — otherwise a half-typed address's slow reply lands last and wins.</summary>
    private async Task DiscoverAsync()
    {
        _discovery?.Cancel();
        var url = Address.Trim();
        if (!IsValidUrl(url))
        {
            _shell.Dispatcher.Post(() =>
            {
                SupportsPairing = true;
                SupportsGitHub = false;
                SupportsKeypair = false;
            });
            return;
        }

        var cts = new CancellationTokenSource();
        _discovery = cts;
        _shell.Dispatcher.Post(() => IsDiscovering = true);
        try
        {
            // Debounce: a phone keyboard produces a burst of keystrokes, and each would otherwise be a probe.
            await Task.Delay(450, cts.Token).ConfigureAwait(false);
            var methods = await AuthDiscovery.GetMethodsAsync(url, cancellationToken: cts.Token).ConfigureAwait(false);
            _shell.Dispatcher.Post(() =>
            {
                SupportsPairing = methods.Pairing;
                SupportsGitHub = methods.GitHub;
                SupportsKeypair = methods.Keypair;
                _gitHubClientId = methods.GitHubClientId;
            });
        }
        catch (OperationCanceledException)
        {
            // superseded by a later keystroke
        }
        catch
        {
            _shell.Dispatcher.Post(() =>
            {
                SupportsPairing = true; // assume the common case until a host says otherwise
                SupportsGitHub = false;
                SupportsKeypair = false;
            });
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _shell.Dispatcher.Post(() => IsDiscovering = false);
            }
        }
    }

    // ---- status ----

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatus => Status.Length > 0;

    // ---- GitHub device flow ----

    [ObservableProperty]
    private bool _isGitHubAuthorizing;

    [ObservableProperty]
    private string _userCode = string.Empty;

    [ObservableProperty]
    private string _verificationUri = string.Empty;

    // ---- device keypair ----

    [ObservableProperty]
    private bool _showKey;

    [ObservableProperty]
    private string _publicKeyLine = string.Empty;

    public IAsyncRelayCommand PairCommand { get; }
    public IAsyncRelayCommand GitHubCommand { get; }
    public IAsyncRelayCommand KeyCommand { get; }
    public IRelayCommand CopyKeyCommand { get; }
    public IRelayCommand OpenVerificationCommand { get; }
    public IRelayCommand CopyUserCodeCommand { get; }
    public IRelayCommand DocsCommand { get; }

    private async Task PairAsync()
    {
        var url = Address.Trim();
        Report("Pairing…", error: false, busy: true);
        try
        {
            // The field takes a pairing code, which is exchanged for a durable per-device token. A
            // pre-issued bootstrap token pasted in the same field still works: if pairing refuses it, we
            // fall through and try it as a token directly.
            var entry = Code.Trim();
            string token;
            var pairingFailed = false;
            try
            {
                var paired = await DevicePairing.PairAsync(url, entry, _shell.DeviceName).ConfigureAwait(false);
                token = paired.Token;
            }
            catch
            {
                token = entry;
                pairingFailed = true;
            }

            if (!await FinishAsync(url, token).ConfigureAwait(false) && pairingFailed)
            {
                Report("That code didn't work — it may have expired. Get a fresh one from the host.", error: true, busy: false);
            }
        }
        catch (Exception ex)
        {
            Report(ex.Message, error: true, busy: false);
        }
    }

    private async Task GitHubAsync()
    {
        var url = Address.Trim();
        if (string.IsNullOrEmpty(_gitHubClientId))
        {
            Report("This host doesn't offer GitHub sign-in.", error: true, busy: false);
            return;
        }

        Report("Starting GitHub sign-in…", error: false, busy: true);
        try
        {
            var code = await GitHubDeviceLogin.StartAsync(_gitHubClientId).ConfigureAwait(false);
            _shell.Dispatcher.Post(() =>
            {
                UserCode = code.UserCode;
                VerificationUri = code.VerificationUri;
                IsGitHubAuthorizing = true;
                Status = string.Empty;
            });
            _shell.OpenUrl(code.VerificationUri);

            var paired = await GitHubDeviceLogin.CompleteAsync(url, _gitHubClientId, code, _shell.DeviceName).ConfigureAwait(false);
            await FinishAsync(url, paired.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report("GitHub sign-in failed: " + ex.Message, error: true, busy: false);
        }
        finally
        {
            _shell.Dispatcher.Post(() => IsGitHubAuthorizing = false);
        }
    }

    private async Task KeyAsync()
    {
        var url = Address.Trim();
        Report("Signing in with this device's key…", error: false, busy: true);
        try
        {
            using (var key = KeypairEnrollment.LoadOrCreateKey())
            {
                var line = KeypairEnrollment.PublicKeyLine(key);
                _shell.Dispatcher.Post(() =>
                {
                    PublicKeyLine = line;
                    ShowKey = true;
                });
            }

            var paired = await KeypairEnrollment.AuthenticateAsync(url, _shell.DeviceName).ConfigureAwait(false);
            await FinishAsync(url, paired.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report("Key sign-in failed: " + ex.Message
                + " Add the key above to the host's authorized_keys, then try again.", error: true, busy: false);
        }
    }

    /// <summary>Connects with the obtained token and — only on success — saves the host. A wrong address
    /// or a stale code never ends up in the device's host list.</summary>
    private async Task<bool> FinishAsync(string url, string token)
    {
        Report("Connecting…", error: false, busy: true);
        var name = string.IsNullOrWhiteSpace(Name) ? new Uri(url).Host : Name.Trim();
        var link = _hosts.Add(new SavedHost(name, url, token));

        var host = await link.ConnectAsync().ConfigureAwait(false);
        if (host is null)
        {
            _hosts.Remove(link);
            Report(link.Error is { Length: > 0 } e ? $"Couldn't reach {name} — {e}" : $"Couldn't reach {name}.", error: true, busy: false);
            return false;
        }

        _shell.Dispatcher.Post(() =>
        {
            IsBusy = false;
            _shell.Haptics.Success();
            _shell.Toast($"Paired with {name}", ToastKind.Success);
            _shell.Pop();
            // Straight into starting a session: pairing is never the goal, it's the step before the goal.
            _sessions.StartNew();
        });
        return true;
    }

    private void Report(string message, bool error, bool busy) => _shell.Dispatcher.Post(() =>
    {
        Status = message;
        StatusIsError = error;
        IsBusy = busy;
    });
}
