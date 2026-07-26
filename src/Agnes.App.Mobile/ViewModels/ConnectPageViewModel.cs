using Agnes.App.Mobile.Services;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>What probing the entered address found.</summary>
public enum HostReach
{
    /// <summary>Nothing typed yet, or not a usable address.</summary>
    Unknown,
    Checking,

    /// <summary>An Agnes host answered.</summary>
    Found,

    /// <summary>Something answered but didn't identify as Agnes — an older host, or the wrong service.</summary>
    Answered,

    /// <summary>Nothing answered.</summary>
    Unreachable,
}

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

    /// <summary>The host certificate fingerprint a scanned QR carried, if any.</summary>
    private readonly string? _fingerprint;

    /// <summary>An HTTP client pinned to <see cref="_fingerprint"/>, or null when there's nothing to pin.</summary>
    private readonly HttpClient? _http;

    public ConnectPageViewModel(
        IAppShell shell,
        HostBook hosts,
        SessionsViewModel sessions,
        string? prefillUrl = null,
        string? prefillCode = null,
        string? sessionId = null,
        bool autoSubmit = false,
        string? fingerprint = null)
    {
        _shell = shell;
        _hosts = hosts;
        _sessions = sessions;
        _sessionId = sessionId;
        _autoSubmit = autoSubmit;
        _fingerprint = string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint.Trim().ToLowerInvariant();

        // Every REST call in this flow — probing, pairing, asking for approval — goes to the same host the
        // hub will, over the same self-signed HTTPS. Without the pin the very first request fails on trust,
        // long before the token we're here to fetch could be used.
        _http = _fingerprint is null ? null : PinnedTls.CreateClient(_fingerprint);
        _address = prefillUrl ?? "https://";
        _code = prefillCode ?? string.Empty;

        PairCommand = new AsyncRelayCommand(PairAsync, () => CanSignIn && Code.Trim().Length > 0);
        GitHubCommand = new AsyncRelayCommand(GitHubAsync, () => CanSignIn);
        KeyCommand = new AsyncRelayCommand(KeyAsync, () => CanSignIn);
        CopyKeyCommand = new RelayCommand(() => _shell.CopyToClipboard(PublicKeyLine, "Public key"));
        OpenVerificationCommand = new RelayCommand(() => _shell.OpenUrl(VerificationUri));
        CopyUserCodeCommand = new RelayCommand(() => _shell.CopyToClipboard(UserCode, "Code"));
        DocsCommand = new RelayCommand(() => _shell.OpenUrl(DocsUrl));
        AskApprovalCommand = new AsyncRelayCommand(AskApprovalAsync, () => CanSignIn && !IsAwaitingApproval);
        CancelApprovalCommand = new RelayCommand(() =>
        {
            _approvalPolling?.Cancel();
            IsAwaitingApproval = false;
            Status = string.Empty;
        });

        if (prefillUrl is not null)
        {
            _ = DiscoverAsync();
        }
    }

    /// <summary>The session a scanned QR came from, opened once pairing succeeds.</summary>
    private readonly string? _sessionId;

    /// <summary>Set for a scanned grant. It was never typed, so there is nothing to review — pair as soon
    /// as the probe confirms the host is there, rather than making the user tap a button.</summary>
    private readonly bool _autoSubmit;

    private bool _autoSubmitted;

    /// <summary>Whether this screen arrived from a scan rather than being typed into.</summary>
    public bool IsFromScan => _autoSubmit;

    private const string DocsUrl = "https://github.com/AdamFrisby/Agnes/blob/main/docs/deployment.md";

    public override string Title => "Connect a host";


    // ---- address ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddressUsable))]
    private string _address;

    partial void OnAddressChanged(string value)
    {
        RaiseCanSignIn();
        _ = DiscoverAsync();
    }

    /// <summary>Whether the typed address is a plausible https host — Connect stays disabled until it is,
    /// so the first failure isn't a network round trip.</summary>
    public bool IsAddressUsable => IsValidUrl(Address.Trim());

    /// <summary>Whether signing in is worth attempting: the address parses *and* something answered at
    /// it. Sending a pairing code to an address with nothing behind it can only produce a misleading
    /// failure, so the buttons stay disabled until the probe finds a host.</summary>
    public bool CanSignIn => IsAddressUsable && Reach is HostReach.Found or HostReach.Answered;

    private void RaiseCanSignIn()
    {
        OnPropertyChanged(nameof(CanSignIn));
        PairCommand.NotifyCanExecuteChanged();
        GitHubCommand.NotifyCanExecuteChanged();
        KeyCommand.NotifyCanExecuteChanged();
        AskApprovalCommand.NotifyCanExecuteChanged();
    }

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
    [NotifyPropertyChangedFor(nameof(ShowPairing))]
    private bool _supportsPairing = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGitHub))]
    private bool _supportsGitHub;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKeypair))]
    private bool _supportsKeypair;

    private string? _gitHubClientId;

    /// <summary>
    /// Probes the entered address: is anything there, and what sign-in does it offer? Re-run on every
    /// keystroke with the previous probe cancelled, so a half-typed address's slow reply can't land last
    /// and win.
    ///
    /// Reachability is reported in its own right, not folded into the sign-in options. An unreachable
    /// host and a host that rejects your code are different problems with different fixes, and the
    /// screen has to say which one you have.
    /// </summary>
    private async Task DiscoverAsync()
    {
        _discovery?.Cancel();
        var url = Address.Trim();
        if (!IsValidUrl(url))
        {
            _shell.Dispatcher.Post(() =>
            {
                Reach = HostReach.Unknown;
                ReachDetail = string.Empty;
                SupportsPairing = true;
                SupportsGitHub = false;
                SupportsKeypair = false;
            });
            return;
        }

        var cts = new CancellationTokenSource();
        _discovery = cts;
        _shell.Dispatcher.Post(() => { IsDiscovering = true; Reach = HostReach.Checking; ReachDetail = string.Empty; });
        try
        {
            // Debounce: a phone keyboard produces a burst of keystrokes, and each would otherwise be a probe.
            await Task.Delay(450, cts.Token).ConfigureAwait(false);
            var probe = await AuthDiscovery.ProbeAsync(url, _http, cts.Token).ConfigureAwait(false);
            _shell.Dispatcher.Post(() =>
            {
                Reach = probe.Outcome switch
                {
                    HostProbeOutcome.Reachable => HostReach.Found,
                    HostProbeOutcome.Answered => HostReach.Answered,
                    _ => HostReach.Unreachable,
                };
                ReachDetail = probe.Error ?? string.Empty;
                SupportsPairing = probe.Methods.Pairing;
                SupportsGitHub = probe.Methods.GitHub;
                SupportsKeypair = probe.Methods.Keypair;
                _gitHubClientId = probe.Methods.GitHubClientId;
                RaiseCanSignIn();

                if (_autoSubmit && !_autoSubmitted && CanSignIn && Code.Trim().Length > 0)
                {
                    _autoSubmitted = true;
                    _ = PairAsync();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // superseded by a later keystroke
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _shell.Dispatcher.Post(() => IsDiscovering = false);
            }
        }
    }

    // ---- reachability ----

    /// <summary>What the address probe found. Drives the line under the address field, and gates the
    /// sign-in buttons so a code is never typed into the void.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnreachable))]
    [NotifyPropertyChangedFor(nameof(IsFound))]
    [NotifyPropertyChangedFor(nameof(IsAnswered))]
    [NotifyPropertyChangedFor(nameof(HasReachNote))]
    [NotifyPropertyChangedFor(nameof(ReachText))]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    [NotifyPropertyChangedFor(nameof(ShowPairing))]
    [NotifyPropertyChangedFor(nameof(ShowGitHub))]
    [NotifyPropertyChangedFor(nameof(ShowKeypair))]
    [NotifyPropertyChangedFor(nameof(ShowAskApproval))]
    private HostReach _reach = HostReach.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReachText))]
    private string _reachDetail = string.Empty;

    public bool IsUnreachable => Reach == HostReach.Unreachable;

    public bool IsFound => Reach == HostReach.Found;

    public bool IsAnswered => Reach == HostReach.Answered;

    /// <summary>Each sign-in method shows only once a host has answered *and* advertised it. Before
    /// that the screen offers nothing to fill in, because nothing would work.</summary>
    public bool ShowPairing => ShowSignIn && SupportsPairing;

    public bool ShowGitHub => ShowSignIn && SupportsGitHub;

    public bool ShowKeypair => ShowSignIn && SupportsKeypair;

    /// <summary>The offer to ask a paired device. It gives way to the digits once a request is out —
    /// which then stay put even if the host stops answering mid-wait, because a request that's already
    /// been made is still approvable and the human still needs the digits to compare.</summary>
    public bool ShowAskApproval => ShowSignIn && !IsAwaitingApproval;

    public bool HasReachNote => Reach is not HostReach.Unknown;

    /// <summary>Whether to offer the sign-in methods at all. Hidden until something answers, so the
    /// screen doesn't invite you to pair with an address that isn't there.</summary>
    public bool ShowSignIn => Reach is HostReach.Found or HostReach.Answered;

    public string ReachText => Reach switch
    {
        HostReach.Checking => "Looking for a host…",
        HostReach.Found => "Agnes host found.",
        HostReach.Answered => "Something answered, but it didn't identify as Agnes. "
            + (ReachDetail.Length > 0 ? ReachDetail : string.Empty),
        HostReach.Unreachable => "Can't reach that address. "
            + (ReachDetail.Length > 0 ? ReachDetail : string.Empty),
        _ => string.Empty,
    };

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

    /// <summary>Ask an already-paired device to vouch for this one — the path when there's no QR to scan.</summary>
    public IAsyncRelayCommand AskApprovalCommand { get; }

    public IRelayCommand CancelApprovalCommand { get; }

    // ---- approval pairing (this device is the one asking) ----

    private CancellationTokenSource? _approvalPolling;

    /// <summary>True while this device is waiting for a human on another device to decide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAskApproval))]
    private bool _isAwaitingApproval;

    /// <summary>
    /// The six digits to compare with the ones shown on the approving device. Derived here from this
    /// device's own key, not taken on trust from the host — that independence is the entire point.
    /// </summary>
    [ObservableProperty]
    private string _verificationCode = string.Empty;

    partial void OnIsAwaitingApprovalChanged(bool value) => AskApprovalCommand.NotifyCanExecuteChanged();

    private async Task AskApprovalAsync()
    {
        var url = Address.Trim();
        _approvalPolling?.Cancel();
        var cts = new CancellationTokenSource();
        _approvalPolling = cts;

        Report("Asking to be approved…", error: false, busy: true);
        try
        {
            var publicKey = PairingApproval.LocalPublicKey();
            var pending = await PairingApproval
                .RequestAsync(url, publicKey, _shell.DeviceName, _http, cts.Token)
                .ConfigureAwait(false);

            _shell.Dispatcher.Post(() =>
            {
                VerificationCode = pending.VerificationCode;
                IsAwaitingApproval = true;
                IsBusy = false;
                Status = "Compare these digits on your other device, then approve there.";
                StatusIsError = false;
            });

            await PollUntilDecidedAsync(url, pending, cts.Token).ConfigureAwait(false);
        }
        catch (PairingRefusedException refused)
        {
            Report(refused.Message, error: true, busy: false);
            _shell.Dispatcher.Post(() => IsAwaitingApproval = false);
        }
        catch (OperationCanceledException)
        {
            // the user backed out
        }
        catch (Exception ex) when (IsUnreachableFailure(ex))
        {
            Report($"Couldn't reach {HostLabel(url)} — {AuthDiscovery.DescribeFailure(ex)}", error: true, busy: false);
            _shell.Dispatcher.Post(() => IsAwaitingApproval = false);
        }
    }

    /// <summary>
    /// Polls until a human decides or the request lapses. Every two seconds: fast enough that approving
    /// feels immediate, slow enough not to hammer an unauthenticated endpoint.
    /// </summary>
    private async Task PollUntilDecidedAsync(string url, PairApprovalPending pending, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < pending.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            var status = await PairingApproval.PollAsync(url, pending.RequestId, _http, cancellationToken)
                .ConfigureAwait(false);

            switch (status.State)
            {
                case PairApprovalState.Approved when status.Token is { Length: > 0 } token:
                    _shell.Dispatcher.Post(() => IsAwaitingApproval = false);
                    await FinishAsync(url, token).ConfigureAwait(false);
                    return;

                case PairApprovalState.Denied:
                    _shell.Dispatcher.Post(() => IsAwaitingApproval = false);
                    Report("That device declined the request.", error: true, busy: false);
                    return;

                case PairApprovalState.Unknown:
                    _shell.Dispatcher.Post(() => IsAwaitingApproval = false);
                    Report("The request expired. Ask again when you're at the other device.", error: true, busy: false);
                    return;
            }
        }

        _shell.Dispatcher.Post(() => IsAwaitingApproval = false);
    }

    /// <summary>
    /// Puts a typed code into the exact shape the host compares against.
    ///
    /// The host checks the code with a fixed-time byte comparison of the trimmed string, so it is both
    /// case- and hyphen-sensitive — and an Android keyboard hands you lowercase by default. Rather than
    /// let that fail as "wrong code", the letters are upper-cased and the grouping hyphen is rebuilt, so
    /// "k7qx m3rt", "K7QXM3RT" and "K7QX-M3RT" all pair.
    /// </summary>
    public static string NormalizeCode(string entry)
    {
        var chars = entry.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        var compact = new string(chars);

        // Only re-group when it's the length the host issues; anything else is passed through so a
        // pre-issued bootstrap token pasted into the same field still reaches the fallback path intact.
        return compact.Length == 8 ? compact[..4] + "-" + compact[4..] : entry.Trim();
    }

    private async Task PairAsync()
    {
        var url = Address.Trim();
        Report("Pairing…", error: false, busy: true);

        // The field takes a pairing code, which is exchanged for a durable per-device token. A
        // pre-issued bootstrap token pasted into the same field also works — but only as a fallback
        // when the host actually answered and rejected the entry as a code. Falling back on *any*
        // failure is what previously turned "your host is unreachable" into "your code is wrong".
        var entry = NormalizeCode(Code);
        string token;
        try
        {
            var paired = await DevicePairing.PairAsync(url, entry, _shell.DeviceName, _http).ConfigureAwait(false);
            token = paired.Token;
        }
        catch (PairingRefusedException refused) when (refused.IsBadCode)
        {
            // Reached the host; it said no. Either the code is stale, or this is a bootstrap token —
            // try it as a token, and if that fails too, say what actually happened.
            if (!await FinishAsync(url, entry).ConfigureAwait(false))
            {
                Report("The host rejected that code. It's single-use and rotates after a few bad tries — "
                    + "take the current one from the host's log.", error: true, busy: false);
            }

            return;
        }
        catch (PairingRefusedException refused)
        {
            Report(refused.Message, error: true, busy: false);
            return;
        }
        catch (Exception ex) when (IsUnreachableFailure(ex))
        {
            Report($"Couldn't reach {HostLabel(url)} — {AuthDiscovery.DescribeFailure(ex)} "
                + "Check the address and that the host is running.", error: true, busy: false);
            return;
        }
        catch (Exception ex)
        {
            Report("Pairing failed: " + ex.Message, error: true, busy: false);
            return;
        }

        await FinishAsync(url, token).ConfigureAwait(false);
    }

    /// <summary>Whether a failure means "nothing answered" rather than "the host said no".</summary>
    private static bool IsUnreachableFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Http.HttpRequestException
                or System.Net.Sockets.SocketException
                or System.Security.Authentication.AuthenticationException
                or TimeoutException
                or TaskCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The host part of a URL, for messages — the full URL is noise once it's in the field above.</summary>
    private static string HostLabel(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

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

            var paired = await GitHubDeviceLogin
                .CompleteAsync(url, _gitHubClientId, code, _shell.DeviceName, _http).ConfigureAwait(false);
            await FinishAsync(url, paired.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachableFailure(ex))
        {
            Report($"Couldn't reach {HostLabel(url)} — {AuthDiscovery.DescribeFailure(ex)}", error: true, busy: false);
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

            var paired = await KeypairEnrollment.AuthenticateAsync(url, _shell.DeviceName, httpClient: _http)
                .ConfigureAwait(false);
            await FinishAsync(url, paired.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachableFailure(ex))
        {
            Report($"Couldn't reach {HostLabel(url)} — {AuthDiscovery.DescribeFailure(ex)}", error: true, busy: false);
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
        var link = _hosts.Add(new SavedHost(name, url, token, _fingerprint));

        var host = await link.ConnectAsync().ConfigureAwait(false);
        if (host is null)
        {
            _hosts.Remove(link);
            Report(link.Error is { Length: > 0 } e ? $"Couldn't reach {name} — {e}" : $"Couldn't reach {name}.", error: true, busy: false);
            return false;
        }

        // A QR scanned from a session carries that session, so the scan lands where it was taken from
        // rather than on a generic "now pick something" screen.
        if (_sessionId is { Length: > 0 } sessionId)
        {
            await AdoptScannedSessionAsync(link, host, sessionId, name).ConfigureAwait(false);
            return true;
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

    /// <summary>Subscribes to the session a scanned QR pointed at and opens it. Falls back to the normal
    /// post-pairing screen if that session is gone — the pairing itself still succeeded.</summary>
    private async Task AdoptScannedSessionAsync(HostLink link, IAgnesHost host, string sessionId, string name)
    {
        try
        {
            var view = await host.SubscribeAsync(sessionId).ConfigureAwait(false);
            _shell.Dispatcher.Post(() =>
            {
                IsBusy = false;
                _shell.Haptics.Success();
                _shell.Toast($"Paired with {name}", ToastKind.Success);
                _shell.Pop();

                var title = view.Info?.WorkingDirectory ?? sessionId;
                var session = _sessions.Build(host, view, title);
                var saved = new SavedSession(link.Name, link.Url, link.Saved.Token, sessionId,
                    view.Info?.AdapterId ?? "agent", title, view.Info?.WorkingDirectory ?? string.Empty);
                _sessions.Adopt(link, session, saved);
            });
        }
        catch
        {
            _shell.Dispatcher.Post(() =>
            {
                IsBusy = false;
                _shell.Toast($"Paired with {name}, but that session is no longer open.", ToastKind.Warning);
                _shell.Pop();
                _sessions.StartNew();
            });
        }
    }

    private void Report(string message, bool error, bool busy) => _shell.Dispatcher.Post(() =>
    {
        Status = message;
        StatusIsError = error;
        IsBusy = busy;
    });
}
