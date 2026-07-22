namespace Agnes.Protocol;

/// <summary>
/// Reconciles a host's capabilities against a connecting client's, so each party can reason about what is
/// usable end to end (see <c>.ideas/00c-client-plugins-and-negotiation.md</c>). Pure logic, transport-
/// agnostic, so it's unit-testable without a hub and reusable by any binding.
/// </summary>
public static class CapabilityNegotiator
{
    /// <summary>Classifies every capability id known to either party as HostOnly / ClientOnly / Both,
    /// carrying <see cref="HostCapability.FailClosed"/> through from the host catalog. A host capability
    /// counts as present only when <see cref="HostCapability.Available"/> is true.</summary>
    public static NegotiatedCapabilities Reconcile(IReadOnlyList<HostCapability> host, ClientCapabilities client)
    {
        var hostAvailable = host.Where(c => c.Available).ToDictionary(c => c.Id, c => c.FailClosed, StringComparer.Ordinal);
        var clientIds = new HashSet<string>(client.CapabilityIds, StringComparer.Ordinal);

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        ids.UnionWith(hostAvailable.Keys);
        ids.UnionWith(clientIds);

        var result = new List<NegotiatedCapability>(ids.Count);
        foreach (var id in ids)
        {
            var onHost = hostAvailable.TryGetValue(id, out var failClosed);
            var onClient = clientIds.Contains(id);
            var support = (onHost, onClient) switch
            {
                (true, true) => CapabilitySupport.Both,
                (true, false) => CapabilitySupport.HostOnly,
                _ => CapabilitySupport.ClientOnly,
            };
            result.Add(new NegotiatedCapability(id, support, FailClosed: onHost && failClosed));
        }

        return new NegotiatedCapabilities(result);
    }
}
