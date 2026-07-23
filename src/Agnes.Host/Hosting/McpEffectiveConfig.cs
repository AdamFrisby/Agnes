using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// Computes the effective-config PREVIEW for a workspace: the union of the servers Agnes manages
/// (<see cref="McpRegistry.EffectiveFor"/>) and the ones an agent CLI already has configured in its OWN native
/// config (via any adapter implementing <see cref="IMcpDiscoveryAdapter"/>). Kept out of <see cref="McpRegistry"/>
/// so the registry stays a pure persistence/scoping component with no dependency on the adapter set — the
/// adapter fan-out and the merge policy live here, as pure-over-their-inputs functions. Native servers are
/// tagged read-only (<see cref="McpServerInfo.NativeConfig"/>); on a name collision the native one wins and is
/// flagged, so a server present in both is surfaced once rather than silently double-listed.
/// </summary>
public static class McpEffectiveConfig
{
    /// <summary>Preview the merged effective set for <paramref name="workspaceId"/> (the workspace directory).
    /// When <paramref name="agentId"/> is given, only that adapter's native config is consulted; otherwise every
    /// discovery-capable adapter is. A detector that fails is skipped — it never breaks the preview.</summary>
    public static async Task<IReadOnlyList<McpServerInfo>> PreviewAsync(
        McpRegistry registry,
        IEnumerable<IAgentAdapter> adapters,
        string? workspaceId,
        string? agentId = null,
        CancellationToken ct = default)
    {
        var managed = registry.EffectiveFor(workspaceId);
        var native = await DetectNativeAsync(adapters, workspaceId, agentId, ct).ConfigureAwait(false);
        return Merge(managed, native);
    }

    /// <summary>Pure merge: Agnes-managed servers whose name isn't also native, unioned with all native servers
    /// (which win on a name collision, carrying their read-only native flag). Sorted by name for stable output.</summary>
    public static IReadOnlyList<McpServerInfo> Merge(
        IReadOnlyList<McpServerInfo> managed, IReadOnlyList<McpServerInfo> native)
    {
        var nativeNames = native.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return managed.Where(m => !nativeNames.Contains(m.Name))
            .Concat(native)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<McpServerInfo>> DetectNativeAsync(
        IEnumerable<IAgentAdapter> adapters, string? workspaceId, string? agentId, CancellationToken ct)
    {
        // Native config is read at a real workspace directory (workspaceId is that path); no directory => none.
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return [];
        }

        var results = new List<McpServerInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // dedupe across adapters, by name
        foreach (var adapter in adapters)
        {
            if (agentId is { Length: > 0 } id && !string.Equals(adapter.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (adapter is not IMcpDiscoveryAdapter discovery)
            {
                continue;
            }

            IReadOnlyList<NativeMcpServer> detected;
            try
            {
                detected = await discovery.DetectNativeConfigAsync(workspaceId, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested == false)
            {
                // A single adapter's detector failing must never break the whole preview — skip it and move on.
                continue;
            }

            foreach (var server in detected)
            {
                if (seen.Add(server.Name))
                {
                    results.Add(ToInfo(adapter.Descriptor.Id, server));
                }
            }
        }

        return results;
    }

    private static McpServerInfo ToInfo(string adapterId, NativeMcpServer native) => new(
        Id: $"native:{adapterId}:{native.Name}",
        Name: native.Name,
        RunAt: "Host",
        Enabled: true,
        Transport: native.Transport,
        Command: native.Command,
        Args: native.Args,
        Env: native.Env,
        Url: native.Url,
        BearerTokenEnv: null,
        ApplyScope: McpApplyScope.AllHosts,
        WorkspaceId: null,
        NativeConfig: true,
        Source: native.SourceLabel);
}
