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
    /// their package registries + git host); Agnes attaches them per sandbox so a compromised or run-away agent
    /// can't exfiltrate or reach internal networks. This is the CodeyBox-lineage, Incus-native egress control.
    /// </summary>
    public IReadOnlyList<string> NetworkAcls { get; init; } = [];

    /// <summary>Unprivileged uid/gid the agent runs as inside the guest.</summary>
    public int GuestUserId { get; init; } = 1000;
    public int GuestGroupId { get; init; } = 1000;
    public string GuestHome { get; init; } = "/home/agnes";

    public TimeSpan GuestReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan VmStopTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
