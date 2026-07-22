namespace Agnes.Host.Hosting;

/// <summary>The client-facing address(es) a transport makes the host reachable at.</summary>
public sealed record TransportEndpoint(IReadOnlyList<string> ClientAddresses, string? DisplayHint);

/// <summary>Context a transport uses to describe how the host is reachable — the addresses the host bound.</summary>
public sealed record HostExposureContext(IReadOnlyList<string> BoundAddresses);

/// <summary>
/// How the host makes itself reachable by clients. Exposed as a plugin-point (AC13) so the
/// direct-vs-relay-vs-tunnel choice flows through the same
/// <see cref="Agnes.Abstractions.IPluginRegistry{TProvider}"/> as agents and sandboxes. The built-in
/// <see cref="DirectTransportProvider"/> is today's behavior — clients connect straight to the host's
/// bound TLS listener; a relay or tunnel transport (see <c>.ideas/connectivity/01-relay-and-tunneling.md</c>)
/// can be added as a plugin later. This point governs reachability/address advertisement only; the actual
/// SignalR hub binding is unchanged.
/// </summary>
public interface ITransportProvider
{
    /// <summary>Stable id, e.g. <c>direct</c>.</summary>
    string Id { get; }

    /// <summary>Human-friendly name.</summary>
    string DisplayName { get; }

    /// <summary>Whether this transport reaches clients via an outbound connection only (no inbound port
    /// to open) — false for direct, true for a relay/tunnel.</summary>
    bool RequiresOutboundOnly { get; }

    /// <summary>Describes the address(es) clients should be given for this transport.</summary>
    TransportEndpoint Describe(HostExposureContext context);
}

/// <summary>Built-in: clients connect directly to the host's own bound listener (today's behavior).</summary>
public sealed class DirectTransportProvider : ITransportProvider
{
    public string Id => "direct";
    public string DisplayName => "Direct";
    public bool RequiresOutboundOnly => false;

    public TransportEndpoint Describe(HostExposureContext context)
        => new(context.BoundAddresses, "Clients connect directly to this host's listener.");
}
