using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// Resolves the externally-reachable address a pairing QR / deep-link should encode, and builds the deep
/// link. This is the same "what's my real address" question <see cref="ITransportProvider"/>'s
/// <see cref="TransportEndpoint"/> already answers for ordinary connections — we reuse it here rather than
/// inventing a second path, so a host reached only through a relay or reverse proxy advertises an address a
/// device on a different network can actually resolve, not its bound LAN/loopback address
/// (see <c>.ideas/connectivity/04-device-linking-and-restore.md</c> AC2/AC3).
/// </summary>
public static class PairingReachability
{
    /// <summary>
    /// The address to encode into a pairing QR/deep-link. Priority: an explicit operator override
    /// (<c>Agnes:PublicUrl</c>, for cases the transport can't infer such as a reverse proxy) always wins;
    /// otherwise the active transport's advertised <see cref="TransportEndpoint.ClientAddresses"/>; finally a
    /// bound-address fallback for a Direct transport that has nothing else. Null only if nothing is known.
    /// </summary>
    public static string? Resolve(
        string? publicUrlOverride, TransportEndpoint? endpoint, IReadOnlyList<string>? boundAddresses = null)
    {
        if (!string.IsNullOrWhiteSpace(publicUrlOverride))
        {
            return publicUrlOverride.Trim();
        }

        var advertised = endpoint?.ClientAddresses.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (!string.IsNullOrWhiteSpace(advertised))
        {
            return advertised;
        }

        return boundAddresses?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    }

    /// <summary>
    /// The <c>agnes://pair</c> deep link a QR encodes, so a scanning device connects to the right host.
    ///
    /// The address alone is a value, not a secret — that form is served unauthenticated at
    /// <c>GET /pair/qr</c> and still requires the scanning device to authenticate separately. When a
    /// <paramref name="grant"/> is supplied the link *is* a credential: it carries a 256-bit one-time
    /// secret minted by an already-paired device, and the QR showing it must be treated accordingly
    /// (which is why the clients that display one can hide it again).
    /// </summary>
    public static string BuildDeepLink(string reachableAddress, string? grant = null, string? sessionId = null)
        => PairingLink.Build(reachableAddress, grant, sessionId);
}

/// <summary>
/// Holds the <see cref="TransportEndpoint"/> the active transport resolved when the host came up, so the
/// pairing endpoint can advertise that reachable address without re-running <c>ExposeAsync</c> (which for a
/// tunnel transport does real, one-time setup work). Populated once at startup; read-mostly thereafter.
/// </summary>
public sealed class HostReachability
{
    /// <summary>The address(es) the active transport exposed at startup, or null before it has come up.</summary>
    public TransportEndpoint? Endpoint { get; set; }

    /// <summary>What the server actually bound to, which is the only source of the scheme and port needed
    /// to turn this machine's interface addresses into candidates a phone could dial.</summary>
    public IReadOnlyList<string> BoundAddresses { get; set; } = [];
}
