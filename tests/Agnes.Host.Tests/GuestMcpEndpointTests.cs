using Agnes.Host.Mcp;

namespace Agnes.Host.Tests;

/// <summary>
/// The bridge-local MCP listener's two dangerous decisions. Both fail silently in the wrong direction — a
/// host that binds nothing but the guest port still "starts", and a plaintext port that serves the hub still
/// "works" — so they are pinned here rather than discovered in production.
/// </summary>
public sealed class GuestMcpEndpointTests
{
    [Fact]
    public void The_guest_url_is_added_to_the_existing_listeners_not_substituted_for_them()
    {
        // Getting this wrong unbinds the TLS listener: the host comes up reachable only by sandboxes.
        var combined = GuestMcpEndpoint.CombineUrls("https://0.0.0.0:5081", "http://10.99.5.1:5099");

        Assert.Equal("https://0.0.0.0:5081;http://10.99.5.1:5099", combined);
    }

    [Fact]
    public void Multiple_existing_listeners_all_survive()
    {
        var combined = GuestMcpEndpoint.CombineUrls("https://0.0.0.0:5081;http://127.0.0.1:5000", "http://10.99.5.1:5099");

        Assert.Equal(
            ["https://0.0.0.0:5081", "http://127.0.0.1:5000", "http://10.99.5.1:5099"],
            combined.Split(';'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void With_nothing_configured_the_guest_url_stands_alone(string? existing)
        => Assert.Equal("http://10.99.5.1:5099", GuestMcpEndpoint.CombineUrls(existing, "http://10.99.5.1:5099"));

    [Fact]
    public void Re_adding_the_same_url_does_not_bind_it_twice()
        => Assert.Equal(
            "https://0.0.0.0:5081;http://10.99.5.1:5099",
            GuestMcpEndpoint.CombineUrls("https://0.0.0.0:5081;http://10.99.5.1:5099", "http://10.99.5.1:5099"));

    [Fact]
    public void The_bind_port_is_read_from_the_url()
        => Assert.Equal(5099, GuestMcpEndpoint.TryGetPort("http://10.99.5.1:5099"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://10.99.5.1")]   // no explicit port — nothing to gate on
    public void An_unusable_bind_url_yields_no_port_so_no_gate_is_installed(string? url)
        => Assert.Null(GuestMcpEndpoint.TryGetPort(url));

    // ---- which channel adds a listener instead of replacing the main one ----
    // Verified against a real Kestrel in all four configurations. Each wrong answer is silent: the host
    // starts, and either serves nothing but MCP or never binds MCP at all.

    [Fact]
    public void With_Kestrel_endpoints_configured_an_explicit_listen_is_the_only_channel_that_binds()
    {
        // Agnes's own dev config. Configured endpoints win outright and hosting addresses are ignored, so
        // appending a URL here would bind nothing.
        Assert.Equal(
            GuestMcpEndpoint.ListenerChannel.KestrelEndpoint,
            GuestMcpEndpoint.ChooseChannel(hostingUrls: null, kestrelEndpointsConfigured: true));
    }

    [Fact]
    public void Kestrel_endpoints_still_win_when_hosting_urls_are_also_set()
        => Assert.Equal(
            GuestMcpEndpoint.ListenerChannel.KestrelEndpoint,
            GuestMcpEndpoint.ChooseChannel("http://0.0.0.0:5081", kestrelEndpointsConfigured: true));

    [Fact]
    public void With_only_hosting_urls_the_address_list_is_appended_to()
    {
        // The Docker image's ASPNETCORE_URLS=…:5081. An explicit Listen would make Kestrel ignore it and the
        // host would come up serving nothing but MCP.
        Assert.Equal(
            GuestMcpEndpoint.ListenerChannel.HostingUrls,
            GuestMcpEndpoint.ChooseChannel("http://0.0.0.0:5081", kestrelEndpointsConfigured: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_listener_configured_at_all_we_decline_rather_than_displace_the_default(string? hostingUrls)
        // Kestrel's default endpoint applies only while both channels are empty, so using either one takes
        // the main listener away. Not binding MCP is recoverable; an unreachable host is not.
        => Assert.Equal(
            GuestMcpEndpoint.ListenerChannel.None,
            GuestMcpEndpoint.ChooseChannel(hostingUrls, kestrelEndpointsConfigured: false));

    [Theory]
    [InlineData("http://127.0.0.1:5117", "127.0.0.1", 5117)]
    [InlineData("http://10.99.5.1:5099", "10.99.5.1", 5099)]
    [InlineData("http://localhost:5117", "127.0.0.1", 5117)]
    [InlineData("http://0.0.0.0:5099", "0.0.0.0", 5099)]
    public void A_bind_url_resolves_to_the_endpoint_to_listen_on(string url, string address, int port)
    {
        var endpoint = GuestMcpEndpoint.TryGetEndpoint(url);

        Assert.NotNull(endpoint);
        Assert.Equal(address, endpoint!.Value.Address.ToString());
        Assert.Equal(port, endpoint.Value.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("http://10.99.5.1")]        // no explicit port
    [InlineData("http://some.name:5099")]   // a name we would have to resolve — don't guess
    public void An_endpoint_we_cannot_resolve_without_guessing_is_refused(string? url)
        => Assert.Null(GuestMcpEndpoint.TryGetEndpoint(url));

    [Fact]
    public void Every_plaintext_listener_is_gated_not_just_the_first()
    {
        // There are two now — the sandbox bridge and loopback. A gate written against one port would serve
        // the hub in the clear on the other.
        var ports = GuestMcpEndpoint.RestrictedPorts("http://10.99.5.1:5099", "http://127.0.0.1:5117");

        Assert.Equal([5099, 5117], ports.Order());
    }

    [Fact]
    public void A_listener_that_is_switched_off_contributes_no_port_to_the_gate()
    {
        var ports = GuestMcpEndpoint.RestrictedPorts(null, "http://127.0.0.1:5117", "");

        Assert.Equal([5117], ports);
    }

    [Theory]
    [InlineData("/mcp-agnes")]
    [InlineData("/MCP-AGNES")]
    [InlineData("/mcp-agnes/message")]
    public void The_mcp_endpoint_is_served_on_the_guest_port(string path)
        => Assert.True(GuestMcpEndpoint.IsAllowedPath(path));

    [Theory]
    [InlineData("/agnes")]             // the SignalR hub — carries device tokens
    [InlineData("/devices")]
    [InlineData("/pair")]
    [InlineData("/credentials/token")]
    [InlineData("/")]
    [InlineData("/mcp")]               // the management REST route, NOT the tool endpoint
    [InlineData("/mcp-agnes-other")]   // must not match by prefix alone
    [InlineData(null)]
    public void Everything_else_is_refused_on_the_plaintext_guest_port(string? path)
        => Assert.False(GuestMcpEndpoint.IsAllowedPath(path));
}
