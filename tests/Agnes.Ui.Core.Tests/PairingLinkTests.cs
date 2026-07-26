using Agnes.Protocol;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The <c>agnes://pair</c> link is the whole out-of-band channel: it is rendered as a QR on the host's
/// screen and read by a camera, and everything the new device knows about the host before it has ever
/// talked to it comes from these few query parameters. Host and client both build and parse it, so its
/// shape is a contract.
/// </summary>
public sealed class PairingLinkTests
{
    private const string Fingerprint = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void Everything_put_in_comes_back_out()
    {
        var link = PairingLink.Build("https://192.168.1.20:5099", "the-grant", "sess-7", Fingerprint);

        Assert.Equal("https://192.168.1.20:5099", PairingLink.HostOf(link));
        Assert.Equal(Fingerprint, PairingLink.FingerprintOf(link));
        Assert.Contains("grant=the-grant", link, StringComparison.Ordinal);
        Assert.Contains("session=sess-7", link, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fingerprint_rides_alongside_an_address_with_no_grant()
    {
        // The unauthenticated /pair/qr form: an address and the certificate to expect, but no credential.
        var link = PairingLink.Build("https://studio.lan:5099", fingerprint: Fingerprint);

        Assert.Equal(Fingerprint, PairingLink.FingerprintOf(link));
        Assert.DoesNotContain("grant=", link, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_without_a_fingerprint_reports_none()
    {
        // A host that predates pinning, or one with a CA-issued certificate that should be validated by
        // chain instead. Either way the client must see "no pin", not an empty string it might pin to.
        var link = PairingLink.Build("https://studio.lan:5099", "the-grant");

        Assert.Null(PairingLink.FingerprintOf(link));
        Assert.DoesNotContain("fp=", link, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_with_reserved_characters_survives_alongside_a_fingerprint()
    {
        // Both values are percent-encoded into the same query, so a path or port in the address must not
        // bleed into the parameter that follows it.
        const string address = "https://relay.example.com:8443/agnes/host-1";
        var link = PairingLink.Build(address, "g", "s", Fingerprint);

        Assert.Equal(address, PairingLink.HostOf(link));
        Assert.Equal(Fingerprint, PairingLink.FingerprintOf(link));
    }

    [Fact]
    public void Garbage_parses_to_nothing_rather_than_throwing()
    {
        // This comes off a camera, so malformed input is routine, not exceptional.
        Assert.Null(PairingLink.FingerprintOf("not a uri"));
        Assert.Null(PairingLink.HostOf("not a uri"));
        Assert.Null(PairingLink.FingerprintOf("agnes://pair"));
    }
}
