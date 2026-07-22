using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>How the host is reachable is a built-in <see cref="ITransportProvider"/> plugin selected from a
/// registry (AC13); Direct is the default, a relay/tunnel can be added as a plugin.</summary>
public class TransportProviderTests
{
    [Fact]
    public void Direct_provider_advertises_the_bound_addresses()
    {
        var direct = new DirectTransportProvider();
        Assert.Equal("direct", direct.Id);
        Assert.False(direct.RequiresOutboundOnly);

        var endpoint = direct.Describe(new HostExposureContext(["https://host.local:5081"]));
        Assert.Equal(["https://host.local:5081"], endpoint.ClientAddresses);
        Assert.False(string.IsNullOrWhiteSpace(endpoint.DisplayHint));
    }

    [Fact]
    public void Registry_selects_the_transport_by_name_and_rejects_unknown()
    {
        IReadOnlyList<ITransportProvider> providers = [new DirectTransportProvider()];
        var registry = new PluginRegistry<ITransportProvider>(providers, t => t.Id);

        Assert.Equal("direct", registry.Find("direct")!.Id);
        Assert.Null(registry.Find("relay")); // not registered yet — a plugin would add it
    }
}
