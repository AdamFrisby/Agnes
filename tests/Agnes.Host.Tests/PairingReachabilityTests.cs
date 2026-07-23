using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The pairing QR / deep-link must encode an address a client on another network can actually reach — the
/// active transport's <see cref="TransportEndpoint"/>, or the <c>Agnes:PublicUrl</c> override — not a bound
/// LAN/loopback address (connectivity/04 AC2/AC3, reusing connectivity/01's TransportEndpoint).
/// </summary>
public class PairingReachabilityTests
{
    [Fact]
    public void A_relay_or_tailnet_endpoint_is_encoded_not_the_bound_lan_address()
    {
        // A relay/tunnel transport advertises an off-LAN client address; the bound address is LAN-only.
        var endpoint = new TransportEndpoint(["https://relay.agnes.dev/h/abc123"], "via relay");

        var reachable = PairingReachability.Resolve(
            publicUrlOverride: null, endpoint: endpoint, boundAddresses: ["https://192.168.1.20:5099"]);

        Assert.Equal("https://relay.agnes.dev/h/abc123", reachable);
        Assert.Equal("agnes://pair?host=https%3A%2F%2Frelay.agnes.dev%2Fh%2Fabc123",
            PairingReachability.BuildDeepLink(reachable!));
    }

    [Fact]
    public void Public_url_override_wins_over_whatever_the_transport_would_infer()
    {
        var endpoint = new TransportEndpoint(["https://relay.agnes.dev/h/abc123"], "via relay");

        var reachable = PairingReachability.Resolve(
            publicUrlOverride: "https://agnes.example.com", endpoint: endpoint, boundAddresses: ["https://192.168.1.20:5099"]);

        Assert.Equal("https://agnes.example.com", reachable);
    }

    [Fact]
    public void Public_url_override_is_trimmed_and_blank_is_ignored()
    {
        var endpoint = new TransportEndpoint(["https://relay.agnes.dev/h/abc123"], null);

        Assert.Equal("https://agnes.example.com",
            PairingReachability.Resolve("  https://agnes.example.com  ", endpoint));
        // Whitespace-only override falls through to the transport, not encoded as-is.
        Assert.Equal("https://relay.agnes.dev/h/abc123", PairingReachability.Resolve("   ", endpoint));
    }

    [Fact]
    public void A_lan_only_direct_transport_still_encodes_its_advertised_address()
    {
        // Direct transport: Describe echoes the bound addresses, so the endpoint carries the LAN address —
        // existing behaviour, unchanged, for a purely local deployment.
        var direct = new DirectTransportProvider();
        var endpoint = direct.Describe(new HostExposureContext(["https://host.local:5099"]));

        var reachable = PairingReachability.Resolve(publicUrlOverride: null, endpoint: endpoint);

        Assert.Equal("https://host.local:5099", reachable);
    }

    [Fact]
    public void Falls_back_to_a_bound_address_then_to_null_when_nothing_is_advertised()
    {
        var empty = new TransportEndpoint([], null);
        Assert.Equal("https://192.168.1.20:5099",
            PairingReachability.Resolve(null, empty, ["https://192.168.1.20:5099"]));
        Assert.Null(PairingReachability.Resolve(null, endpoint: null, boundAddresses: null));
    }
}
