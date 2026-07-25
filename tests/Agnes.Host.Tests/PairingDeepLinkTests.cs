using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The deep link is what a QR actually carries, so its shape is a contract between the host, the
/// desktop that renders it and the phone that scans it.
/// </summary>
public sealed class PairingDeepLinkTests
{
    [Fact]
    public void Without_a_grant_the_link_is_only_an_address()
    {
        var link = PairingReachability.BuildDeepLink("https://studio.lan:5099");

        // This form is served unauthenticated at /pair/qr, so it must never carry a credential.
        Assert.DoesNotContain("grant=", link, StringComparison.Ordinal);
        Assert.Contains("host=https%3A%2F%2Fstudio.lan%3A5099", link, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_and_session_ride_in_the_query_string()
    {
        var grants = new PairingGrants();
        var grant = grants.Mint("https://studio.lan:5099", sessionId: "sess-1");

        var uri = new Uri(grant.DeepLink);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal("agnes", uri.Scheme);
        Assert.Equal("https://studio.lan:5099", query["host"]);
        Assert.Equal(grant.Secret, query["grant"]);
        Assert.Equal("sess-1", query["session"]);
    }

    [Fact]
    public void An_address_with_reserved_characters_survives_the_round_trip()
    {
        // A relay address can carry a path and a port; naive concatenation would corrupt it.
        const string address = "https://relay.example.com:8443/agnes/host-1";
        var link = PairingReachability.BuildDeepLink(address, "the-grant");

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);

        Assert.Equal(address, query["host"]);
        Assert.Equal("the-grant", query["grant"]);
    }
}
