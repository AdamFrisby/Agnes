using Agnes.Client;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>The Devices page's own state: pairing a new device from here, rather than from a session tab.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// A pairing code for the connected host with no session attached: the new device lands on the host's
    /// session list. Built lazily — asking for it mints a one-time grant, so nothing is minted until the
    /// button is pressed — and it resolves the host at that moment, so it follows whichever host the
    /// Settings page is showing.
    /// </summary>
    public ConnectQrViewModel PairingQr => _pairingQr ??= new ConnectQrViewModel(
        () => ActiveHttpHost() is { } t ? (t.Url, t.Token) : null,
        () => null,
        _dispatcher,
        () => ActiveHttpHost() is { Fingerprint: { Length: > 0 } pin } ? PinnedTls.CreateClient(pin) : null);

    private ConnectQrViewModel? _pairingQr;
}
