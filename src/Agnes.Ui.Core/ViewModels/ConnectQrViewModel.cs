using System.Collections.ObjectModel;
using Agnes.Client;
using Agnes.Protocol;
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
    private readonly Func<System.Net.Http.HttpClient?>? _httpClientFactory;

    /// <param name="host">Where to mint from, resolved late — a session's host isn't known at construction.</param>
    /// <param name="sessionId">
    /// Carried into the link so a scanning device lands in this session, not just on the host. Resolved
    /// late for the same reason as the host: this view model is built when its tab's view loads, which is
    /// before the session it belongs to exists. Capturing the id then would capture null every time, and
    /// the QR would silently pair the phone to the host without opening anything.
    /// </param>
    /// <param name="httpClientFactory">Optional, resolved late (like <paramref name="host"/>): supplies the
    /// HTTP client the pairing REST calls use. A self-signed host is reachable only over a certificate-pinned
    /// client, and the pin isn't known until the tab connects — which is after this view model is built — so
    /// this is a factory, not a captured client. Also the seam tests use to drive an in-process host.</param>
    public ConnectQrViewModel(
        Func<(string HostUrl, string Token)?> host, Func<string?> sessionId, IUiDispatcher dispatcher,
        Func<System.Net.Http.HttpClient?>? httpClientFactory = null)
    {
        _host = host;
        _sessionId = sessionId;
        _dispatcher = dispatcher;
        _httpClientFactory = httpClientFactory;

        // The choice appears and disappears with the list itself, rather than only where the list happens
        // to be filled — a property derived from a collection has to track the collection.
        Addresses.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAddressChoice));

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

    /// <summary>
    /// Addresses this host reports being reachable at, for when the one it advertises isn't the one the
    /// scanning device can route to — a host bound to loopback, or a phone that's on Tailscale rather
    /// than the LAN.
    /// </summary>
    public ObservableCollection<string> Addresses { get; } = [];

    public bool HasAddressChoice => Addresses.Count > 1;

    /// <summary>
    /// The address currently encoded. Setting it re-encodes the *same* grant against the new address:
    /// the secret is minted by the host and redeemed wherever the device reaches it, so switching costs
    /// no round trip and invalidates nothing.
    /// </summary>
    [ObservableProperty]
    private string _address = string.Empty;

    /// <summary>Set while the view model assigns <see cref="Address"/> itself, so adopting the address the
    /// host already encoded doesn't re-encode an identical link.</summary>
    private bool _settingAddress;

    partial void OnAddressChanged(string value)
    {
        if (_settingAddress || _secret is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        DeepLink = PairingLink.Build(value, _secret, _grantSessionId);
        Matrix = QrMatrix.Encode(DeepLink);
    }

    public IAsyncRelayCommand ShowCommand { get; }

    public IAsyncRelayCommand HideCommand { get; }

    private string? _secret;

    /// <summary>The session the live grant was minted for, kept so re-encoding against another address
    /// carries it too rather than quietly dropping the "and open this session" half of the link.</summary>
    private string? _grantSessionId;

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
            var sessionId = _sessionId();
            var grant = await PairingManagement
                .MintGrantAsync(target.HostUrl, target.Token, sessionId, _httpClientFactory?.Invoke(), timeout.Token)
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
                _grantSessionId = sessionId;
                DeepLink = grant.DeepLink;
                ExpiresAt = grant.ExpiresAt;
                Matrix = QrMatrix.Encode(grant.DeepLink);

                Addresses.Clear();
                foreach (var candidate in grant.Addresses ?? [])
                {
                    Addresses.Add(candidate);
                }

                // Whatever the host chose to encode is the current selection, even if it isn't one of the
                // candidates it listed — assigning it here must not re-encode a link we already have.
                var chosen = PairingLink.HostOf(grant.DeepLink) ?? target.HostUrl;
                if (!Addresses.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                {
                    Addresses.Insert(0, chosen);
                }

                _settingAddress = true;
                Address = chosen;
                _settingAddress = false;

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
        _grantSessionId = null;

        _dispatcher.Post(() =>
        {
            Matrix = null;
            DeepLink = string.Empty;
            ExpiresAt = null;
            Error = string.Empty;
            Addresses.Clear();
            _settingAddress = true;
            Address = string.Empty;
            _settingAddress = false;
            IsVisible = false;
        });

        if (secret is null || _host() is not { } target)
        {
            return;
        }

        try
        {
            await PairingManagement.RevokeGrantAsync(target.HostUrl, target.Token, secret, _httpClientFactory?.Invoke())
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the grant expires on its own shortly regardless, and failing to reach the host
            // must not leave the QR stuck on screen.
        }
    }
}
