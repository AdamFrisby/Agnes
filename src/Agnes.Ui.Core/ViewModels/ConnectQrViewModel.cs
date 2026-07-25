using Agnes.Client;
using Agnes.Ui.Core.Qr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// A displayed pairing QR: mint it, show it, hide it.
///
/// The QR carries a 256-bit one-time grant, so **it is a credential, not an address** — a screen
/// showing one is a screen showing a password. That shapes the whole design here: it is never shown
/// until asked for, hiding it revokes the grant server-side rather than merely stopping drawing it, and
/// it stops being valid on its own after a few minutes whether or not anyone remembers to hide it.
///
/// Shared by every head, so the desktop's session QR and any future client surface behave identically.
/// </summary>
public sealed partial class ConnectQrViewModel : ObservableObject
{
    private readonly Func<(string HostUrl, string Token)?> _host;
    private readonly Func<string?> _sessionId;
    private readonly IUiDispatcher _dispatcher;

    /// <param name="host">Where to mint from, resolved late — a session's host isn't known at construction.</param>
    /// <param name="sessionId">
    /// Carried into the link so a scanning device lands in this session, not just on the host. Resolved
    /// late for the same reason as the host: this view model is built when its tab's view loads, which is
    /// before the session it belongs to exists. Capturing the id then would capture null every time, and
    /// the QR would silently pair the phone to the host without opening anything.
    /// </param>
    public ConnectQrViewModel(Func<(string HostUrl, string Token)?> host, Func<string?> sessionId, IUiDispatcher dispatcher)
    {
        _host = host;
        _sessionId = sessionId;
        _dispatcher = dispatcher;

        ShowCommand = new AsyncRelayCommand(ShowAsync, () => !IsVisible && !IsBusy);
        HideCommand = new AsyncRelayCommand(HideAsync, () => IsPanelOpen);
    }

    /// <summary>The QR grid to draw, or null when nothing is on screen.</summary>
    [ObservableProperty]
    private QrMatrix? _matrix;

    /// <summary>The link encoded, shown as text so it can be read out or copied when a camera won't focus.</summary>
    [ObservableProperty]
    private string _deepLink = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>
    /// Whether the panel is on screen at all — a QR to scan, or an explanation of why there isn't one.
    /// A failure that only sets <see cref="Error"/> with nothing bound to it is indistinguishable from
    /// the menu item doing nothing, which is exactly how this failed in the field.
    /// </summary>
    public bool IsPanelOpen => IsVisible || HasError;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Whether a live grant is on screen right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    private bool _isVisible;

    /// <summary>When the displayed grant stops working by itself.</summary>
    [ObservableProperty]
    private DateTimeOffset? _expiresAt;

    public IAsyncRelayCommand ShowCommand { get; }

    public IAsyncRelayCommand HideCommand { get; }

    private string? _secret;

    partial void OnIsVisibleChanged(bool value)
    {
        ShowCommand.NotifyCanExecuteChanged();
        HideCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorChanged(string value) => HideCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => ShowCommand.NotifyCanExecuteChanged();

    /// <summary>How long to wait for a host to mint. Short on purpose: this runs from a menu item whose
    /// enabled state tracks the request, so a host that accepts the connection and then goes quiet would
    /// otherwise leave that item disabled for HttpClient's 100-second default with nothing on screen.</summary>
    private static readonly TimeSpan MintTimeout = TimeSpan.FromSeconds(15);

    private async Task ShowAsync()
    {
        if (_host() is not { } target)
        {
            Error = "Connect to a host first.";
            return;
        }

        _dispatcher.Post(() => { IsBusy = true; Error = string.Empty; });
        try
        {
            using var timeout = new CancellationTokenSource(MintTimeout);
            var grant = await PairingManagement
                .MintGrantAsync(target.HostUrl, target.Token, _sessionId(), cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                IsBusy = false;
                if (grant is null)
                {
                    Error = "This host can't issue a pairing QR — it may be an older version, "
                        + "or have no externally-reachable address configured.";
                    return;
                }

                _secret = grant.Secret;
                DeepLink = grant.DeepLink;
                ExpiresAt = grant.ExpiresAt;
                Matrix = QrMatrix.Encode(grant.DeepLink);
                IsVisible = true;
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() =>
            {
                IsBusy = false;
                Error = $"{target.HostUrl} didn't answer within {MintTimeout.TotalSeconds:0} seconds.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                IsBusy = false;
                Error = "Couldn't get a pairing code: " + ex.Message;
            });
        }
    }

    /// <summary>
    /// Hides the QR and revokes its grant. Clearing the pixels alone would leave a live credential
    /// behind for the rest of its lifetime — the point of hiding is that it stops working.
    /// </summary>
    private async Task HideAsync()
    {
        var secret = _secret;
        _secret = null;

        _dispatcher.Post(() =>
        {
            Matrix = null;
            DeepLink = string.Empty;
            ExpiresAt = null;
            Error = string.Empty;
            IsVisible = false;
        });

        if (secret is null || _host() is not { } target)
        {
            return;
        }

        try
        {
            await PairingManagement.RevokeGrantAsync(target.HostUrl, target.Token, secret).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the grant expires on its own shortly regardless, and failing to reach the host
            // must not leave the QR stuck on screen.
        }
    }
}
