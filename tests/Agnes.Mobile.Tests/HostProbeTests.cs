using System.Net;
using System.Net.Sockets;
using Agnes.Client;

namespace Agnes.Mobile.Tests;

/// <summary>
/// An unreachable host and a host that rejects your credential are different problems with different
/// fixes. Collapsing them is what made the connect screen blame a correct pairing code for an address
/// that had nothing behind it.
/// </summary>
public sealed class HostProbeTests
{
    [Fact]
    public async Task An_address_with_nothing_behind_it_reports_unreachable()
    {
        // Port 1 on loopback: reliably refused, no network round trip.
        var probe = await AuthDiscovery.ProbeAsync("http://127.0.0.1:1");

        Assert.Equal(HostProbeOutcome.Unreachable, probe.Outcome);
        Assert.False(probe.CanReach);
        Assert.False(string.IsNullOrWhiteSpace(probe.Error));
    }

    [Fact]
    public async Task An_unresolvable_name_reports_unreachable_rather_than_throwing()
    {
        var probe = await AuthDiscovery.ProbeAsync("https://not-a-real-host.invalid");

        Assert.Equal(HostProbeOutcome.Unreachable, probe.Outcome);
    }

    [Fact]
    public void A_refused_connection_is_described_in_plain_language()
    {
        var ex = new HttpRequestException("boom", new SocketException((int)SocketError.ConnectionRefused));

        // Not "An error occurred while sending the request", which names the symptom and not the cause.
        Assert.Contains("listening", AuthDiscovery.DescribeFailure(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_untrusted_certificate_says_so_and_says_what_to_do()
    {
        var ex = new HttpRequestException("boom",
            new System.Security.Authentication.AuthenticationException("cert"));

        var description = AuthDiscovery.DescribeFailure(ex);

        Assert.Contains("certificate", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CA", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_code_is_distinguishable_from_a_host_that_cannot_pair()
    {
        var badCode = new PairingRefusedException(HttpStatusCode.Unauthorized, "no");
        var notAgnes = new PairingRefusedException(HttpStatusCode.NotFound, "no");

        Assert.True(badCode.IsBadCode);
        Assert.False(notAgnes.IsBadCode); // must not be reported as "check your pairing code"
    }
}
