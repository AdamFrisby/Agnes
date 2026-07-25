using System.Net;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// Which addresses a host offers a client for a pairing QR. Local addresses are injected so these
/// describe the ordering rules rather than whatever interfaces the test machine happens to have.
/// </summary>
public sealed class HostAddressesTests
{
    private static IReadOnlyList<string> Candidates(
        string? publicUrl = null,
        IReadOnlyList<string>? advertised = null,
        IReadOnlyList<string>? bound = null,
        params string[] locals)
        => HostAddresses.Candidates(
            publicUrl,
            advertised is null ? null : new TransportEndpoint(advertised, "test"),
            bound ?? ["https://0.0.0.0:5099"],
            () => locals.Select(IPAddress.Parse));

    [Fact]
    public void A_wildcard_binding_becomes_the_machines_real_addresses()
    {
        // "https://0.0.0.0:5099" tells a phone nothing; the interfaces behind it are the whole point.
        var candidates = Candidates(locals: ["192.168.1.20"]);

        Assert.Contains("https://192.168.1.20:5099", candidates);
        Assert.DoesNotContain(candidates, c => c.Contains("0.0.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lan_address_is_offered_before_a_tailscale_one()
    {
        // Both work; the LAN one is the common case, so it leads. Tailscale's 100.64/10 is right behind
        // it because it's the answer when the phone isn't on the LAN at all.
        var candidates = Candidates(locals: ["100.101.102.103", "192.168.1.20"]);

        Assert.True(
            candidates.ToList().IndexOf("https://192.168.1.20:5099")
            < candidates.ToList().IndexOf("https://100.101.102.103:5099"));
    }

    [Fact]
    public void Loopback_comes_last_even_when_the_transport_advertised_it()
    {
        // The case that prompted all this: a host bound to 127.0.0.1 advertises 127.0.0.1, which is
        // correct for the desktop beside it and useless for the phone.
        var candidates = Candidates(
            advertised: ["https://127.0.0.1:5099"],
            bound: ["https://127.0.0.1:5099"],
            locals: ["192.168.1.20"]);

        Assert.Equal("https://192.168.1.20:5099", candidates[0]);
        Assert.True(HostAddresses.IsLoopback(candidates[^1]));
        Assert.Contains(candidates, HostAddresses.IsLoopback); // still offered, just not first
    }

    [Fact]
    public void An_operator_override_wins()
    {
        // Agnes:PublicUrl exists for what the host cannot infer — a reverse proxy, a port-forward.
        var candidates = Candidates(publicUrl: "https://agnes.example.com", locals: ["192.168.1.20"]);

        Assert.Equal("https://agnes.example.com", candidates[0]);
    }

    [Fact]
    public void Ipv6_is_bracketed_so_the_result_is_a_usable_url()
    {
        var candidates = Candidates(locals: ["fd00::1"]);

        Assert.Contains("https://[fd00::1]:5099", candidates);
    }

    [Fact]
    public void Link_local_and_loopback_interfaces_are_not_offered()
    {
        // fe80:: needs a scope id to dial and 127.0.0.1 is added deliberately at the end, not per-NIC.
        var candidates = Candidates(locals: ["fe80::1", "127.0.0.1", "192.168.1.20"]);

        Assert.DoesNotContain(candidates, c => c.Contains("fe80", StringComparison.OrdinalIgnoreCase));
        Assert.Single(candidates, HostAddresses.IsLoopback);
    }

    [Fact]
    public void Nothing_is_offered_twice()
    {
        var candidates = Candidates(
            publicUrl: "https://192.168.1.20:5099",
            advertised: ["https://192.168.1.20:5099"],
            locals: ["192.168.1.20"]);

        Assert.Equal(candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count(), candidates.Count);
    }

    [Fact]
    public void With_no_binding_to_learn_a_port_from_only_what_we_were_told_is_offered()
    {
        // Nothing to infer a scheme or port from, so inventing interface URLs would be guesswork.
        var candidates = HostAddresses.Candidates(
            null, new TransportEndpoint(["https://relay.example.com/h1"], "relay"), [],
            () => [IPAddress.Parse("192.168.1.20")]);

        Assert.Equal(["https://relay.example.com/h1"], candidates);
    }

    [Fact]
    public void FirstRoutable_skips_loopback_and_gives_up_gracefully()
    {
        Assert.Equal("https://192.168.1.20:5099",
            HostAddresses.FirstRoutable(["https://127.0.0.1:5099", "https://192.168.1.20:5099"]));
        Assert.Null(HostAddresses.FirstRoutable(["https://localhost:5099", "https://127.0.0.1:5099"]));
    }
}
