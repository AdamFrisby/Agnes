using Agnes.Client;

namespace Agnes.Integration.Tests;

/// <summary>
/// Address → transport routing for the client pool (multi-server support, connectivity/02): a host is added the
/// same way whatever its address, and the transport is chosen purely from the scheme — a plain URL is Direct, an
/// <c>agnes-relay://</c> address selects the relay tunnel, and a tailnet <c>*.ts.net</c> name is Tailscale.
/// </summary>
public sealed class ClientTransportRoutingTests
{
    [Theory]
    [InlineData("http://192.168.1.5:5080", ClientTransportKind.Direct)]
    [InlineData("https://myhost.example.com", ClientTransportKind.Direct)]
    [InlineData("agnes-relay://relay.example.com:5100/hostid?fp=abcd", ClientTransportKind.Relay)]
    [InlineData("AGNES-RELAY://relay.example.com:5100/hostid?fp=abcd", ClientTransportKind.Relay)]
    [InlineData("https://laptop.tailnet-1234.ts.net", ClientTransportKind.Tailscale)]
    [InlineData("https://laptop.tailnet-1234.TS.NET:5080", ClientTransportKind.Tailscale)]
    public void Classify_picks_the_transport_from_the_address_scheme(string address, ClientTransportKind expected)
        => Assert.Equal(expected, ClientTransport.Classify(address));

    [Fact]
    public void ProviderId_matches_the_host_side_transport_ids()
    {
        Assert.Equal("direct", ClientTransport.ProviderId(ClientTransportKind.Direct));
        Assert.Equal("agnes-relay", ClientTransport.ProviderId(ClientTransportKind.Relay));
        Assert.Equal("tailscale", ClientTransport.ProviderId(ClientTransportKind.Tailscale));
    }

    [Fact]
    public async Task A_relay_address_selects_the_relay_transport_and_reports_the_routed_host_id()
    {
        await using var relay = new HostConnection("agnes-relay://relay.example.com:5100/host-xyz?fp=aa", "token");

        Assert.Equal(ClientTransportKind.Relay, relay.Transport);
        // The stable id is the routed host-id, NOT the synthetic relay URL — so it never collides with a
        // same-addressed host reached through a different relay.
        Assert.Equal("host-xyz", relay.HostId);
    }

    [Fact]
    public async Task A_plain_url_selects_the_direct_transport()
    {
        await using var direct = new HostConnection("http://192.168.0.10:5080", "token");

        Assert.Equal(ClientTransportKind.Direct, direct.Transport);
        Assert.Equal("http://192.168.0.10:5080", direct.HostId);
    }
}
