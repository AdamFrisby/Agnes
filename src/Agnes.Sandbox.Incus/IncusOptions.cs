namespace Agnes.Sandbox.Incus;

/// <summary>Configuration for the Incus backend (all overridable via host config).</summary>
public sealed record IncusOptions
{
    public string BinaryPath { get; init; } = "incus";
    public string ProjectName { get; init; } = "agnes";
    public string StoragePoolName { get; init; } = "default";
    public string DefaultImage { get; init; } = "images:ubuntu/24.04/cloud";

    /// <summary>Host bridge for the sandbox NIC.</summary>
    public string Bridge { get; init; } = "incusbr0";

    /// <summary>
    /// Incus network ACL names to attach to every sandbox NIC (<c>security.acls</c>). Empty (default) = open
    /// egress. The operator defines the ACLs in Incus (e.g. a default-deny egress policy that allowlists only
    /// their package registries + git host); Agnes attaches them per sandbox. This is the Incus-native egress
    /// option; the <see cref="NetworkProfiles"/> / host-nftables approach below is the CodeyBox-compatible one.
    /// </summary>
    public IReadOnlyList<string> NetworkAcls { get; init; } = [];

    /// <summary>
    /// Named network profiles → the host bridge that carries each profile's egress policy. This mirrors
    /// CodeyBox's model: an operator runs its <c>setup-host-networks.sh</c> to create one filtered bridge per
    /// profile (host-kernel nftables allowlist — which a sudo agent inside the VM can't flush), then maps profile
    /// names to those bridges here. The sandbox NIC is attached to the profile's bridge, so the bridge choice
    /// *is* the egress policy. Empty (default) = every sandbox uses <see cref="Bridge"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> NetworkProfiles { get; init; } = new Dictionary<string, string>();

    /// <summary>The profile from <see cref="NetworkProfiles"/> to use when a sandbox doesn't request one. Null =
    /// fall back to <see cref="Bridge"/>.</summary>
    public string? DefaultNetworkProfile { get; init; }

    /// <summary>Resolves the host bridge for a sandbox: an explicit bridge wins, else the named profile's bridge
    /// (requested, or the default), else the plain <see cref="Bridge"/>.</summary>
    public string ResolveBridge(string? explicitBridge, string? profile = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitBridge))
        {
            return explicitBridge!;
        }

        var wanted = !string.IsNullOrWhiteSpace(profile) ? profile : DefaultNetworkProfile;
        if (!string.IsNullOrWhiteSpace(wanted) && NetworkProfiles.TryGetValue(wanted!, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        return Bridge;
    }

    /// <summary>Unprivileged uid/gid the agent runs as inside the guest.</summary>
    public int GuestUserId { get; init; } = 1000;
    public int GuestGroupId { get; init; } = 1000;
    public string GuestHome { get; init; } = "/home/agnes";

    public TimeSpan GuestReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan VmStopTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
