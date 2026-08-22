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
