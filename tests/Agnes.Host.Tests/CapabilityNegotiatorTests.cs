using Agnes.Protocol;

namespace Agnes.Host.Tests;

/// <summary>The reconciliation that lets client and host each learn what the other supports
/// (see .ideas/00c-client-plugins-and-negotiation.md).</summary>
public class CapabilityNegotiatorTests
{
    private static ClientCapabilities Client(params string[] capabilityIds)
        => new("c1", "desktop", SupportsDynamicPlugins: true, PluginPointIds: [], CapabilityIds: capabilityIds);

    [Fact]
    public void Classifies_host_only_client_only_and_both()
    {
        IReadOnlyList<HostCapability> host =
        [
            new("agent-adapter", Available: true, FailClosed: true),   // host only
            new("notifications", Available: true, FailClosed: false),  // both
        ];
        var client = Client("notifications", "client.tool-renderer");   // notifications=both, tool-renderer=client only

        var result = CapabilityNegotiator.Reconcile(host, client);

        CapabilitySupport Support(string id) => result.Capabilities.Single(c => c.Id == id).Support;
        Assert.Equal(CapabilitySupport.HostOnly, Support("agent-adapter"));
        Assert.Equal(CapabilitySupport.Both, Support("notifications"));
        Assert.Equal(CapabilitySupport.ClientOnly, Support("client.tool-renderer"));
    }

    [Fact]
    public void A_two_sided_feature_is_Both_only_when_each_side_has_its_half()
    {
        IReadOnlyList<HostCapability> hostWithTrigger = [new("notifications", Available: true, FailClosed: false)];
        IReadOnlyList<HostCapability> hostWithout = [];

        // Both sides present → Both.
        Assert.Equal(CapabilitySupport.Both,
            CapabilityNegotiator.Reconcile(hostWithTrigger, Client("notifications")).Capabilities.Single().Support);

        // Client only → ClientOnly (not usable end to end).
        Assert.Equal(CapabilitySupport.ClientOnly,
            CapabilityNegotiator.Reconcile(hostWithout, Client("notifications")).Capabilities.Single().Support);

        // Host only → HostOnly.
        Assert.Equal(CapabilitySupport.HostOnly,
            CapabilityNegotiator.Reconcile(hostWithTrigger, Client()).Capabilities.Single().Support);
    }

    [Fact]
    public void A_host_capability_marked_unavailable_is_not_treated_as_present()
    {
        IReadOnlyList<HostCapability> host = [new("sandbox-provider", Available: false, FailClosed: false)];
        var result = CapabilityNegotiator.Reconcile(host, Client("sandbox-provider"));
        Assert.Equal(CapabilitySupport.ClientOnly, result.Capabilities.Single().Support);
    }

    [Fact]
    public void FailClosed_is_carried_through_from_the_host_catalog()
    {
        IReadOnlyList<HostCapability> host = [new("agent-adapter", Available: true, FailClosed: true)];
        var result = CapabilityNegotiator.Reconcile(host, Client());
        Assert.True(result.Capabilities.Single().FailClosed);
    }
}
