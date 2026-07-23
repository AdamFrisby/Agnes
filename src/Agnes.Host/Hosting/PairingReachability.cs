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

    /// <summary>The <c>agnes://pair</c> deep link over a reachable address — what a QR encodes so a scanning
    /// device connects to the right host. The address is a value, not a secret; the pairing code is entered
    /// or vouched for separately by the already-trusted device.</summary>
    public static string BuildDeepLink(string reachableAddress)
        => "agnes://pair?host=" + Uri.EscapeDataString(reachableAddress);
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
}
