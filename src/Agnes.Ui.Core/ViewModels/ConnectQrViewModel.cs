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
    private readonly string? _sessionId;
    private readonly IUiDispatcher _dispatcher;

    /// <param name="host">Where to mint from, resolved late — a session's host isn't known at construction.</param>
    /// <param name="sessionId">Carried into the link so a scanning device lands in this session, not just on the host.</param>
    public ConnectQrViewModel(Func<(string HostUrl, string Token)?> host, string? sessionId, IUiDispatcher dispatcher)
    {
        _host = host;
        _sessionId = sessionId;
        _dispatcher = dispatcher;

        ShowCommand = new AsyncRelayCommand(ShowAsync, () => !IsVisible && !IsBusy);
        HideCommand = new AsyncRelayCommand(HideAsync, () => IsVisible);
    }

    /// <summary>The QR grid to draw, or null when nothing is on screen.</summary>
    [ObservableProperty]
    private QrMatrix? _matrix;

    /// <summary>The link encoded, shown as text so it can be read out or copied when a camera won't focus.</summary>
    [ObservableProperty]
    private string _deepLink = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Whether a live grant is on screen right now.</summary>
    [ObservableProperty]
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

    partial void OnIsBusyChanged(bool value) => ShowCommand.NotifyCanExecuteChanged();

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
            var grant = await PairingManagement
                .MintGrantAsync(target.HostUrl, target.Token, _sessionId)
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
