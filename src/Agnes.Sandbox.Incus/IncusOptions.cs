namespace Agnes.Sandbox.Incus;

/// <summary>Configuration for the Incus backend (all overridable via host config).</summary>
public sealed record IncusOptions
{
    public string BinaryPath { get; init; } = "incus";
    public string ProjectName { get; init; } = "agnes";
    public string StoragePoolName { get; init; } = "default";
    public string DefaultImage { get; init; } = "images:ubuntu/24.04/cloud";

    /// <summary>
    /// Prefix for the names of the instances this provider creates (a random suffix follows). Configurable
    /// so a live probe — or a second daemon sharing one Incus — can name its VMs apart from the operator's
    /// real session VMs: with a single prefix, "the instance this test made" and "the instance somebody is
    /// working in" are indistinguishable from the outside, and cleanup is a guess.
    /// </summary>
    public string InstancePrefix { get; init; } = "agnes-";

    /// <summary>Host bridge for the sandbox NIC.</summary>
    public string Bridge { get; init; } = "incusbr0";

    /// <summary>Default resource caps (RAM/disk/CPU) for a session's sandbox VM, overridable per session via
    /// <see cref="Agnes.Sandbox.SandboxSpec.ResourceOverride"/>. Bound from host config; unset = 2 CPU / 12 GiB
    /// RAM / 16 GiB disk.</summary>
    public Agnes.Sandbox.SandboxResourceLimits DefaultLimits { get; init; } = new();

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

    // ---- graphical sandboxes (SandboxSpec.Display) ----

    /// <summary>The image alias a sandbox with a <see cref="Agnes.Sandbox.GraphicalDisplay"/> launches from.
    /// Baked separately from the headless baseline because it carries an X server and a session.</summary>
    public string GraphicalImage { get; init; } = "agnes-graphical";

    /// <summary>Resource floor applied when a sandbox asks for a display, before the per-session override.
    /// The disk is raised because Incus refuses to launch an image larger than the volume, and the
    /// graphical image is ~1.5 GiB bigger than the headless one.</summary>
    public SandboxResourceOverride GraphicalResourceOverride { get; init; } = new() { DiskBytes = 24L * 1024 * 1024 * 1024 };

    /// <summary>
    /// Where the per-instance display bus sockets live. Must be traversable by the uid QEMU runs as
    /// (see <see cref="DisplayBusQemuUser"/>) — which rules out <c>$XDG_RUNTIME_DIR</c>, whose 0700 mode
    /// belongs to systemd. Access control is the bus policy's uid allowlist, not the directory mode.
    /// </summary>
    public string DisplayRuntimeDirectory { get; init; } = "/tmp/agnes-display";

    /// <summary>The unix user Incus's QEMU runs as (<c>-run-with user=…</c>), allowed on the display bus.</summary>
    public string DisplayBusQemuUser { get; init; } = "incus";

    public string DbusDaemonPath { get; init; } = "dbus-daemon";

    /// <summary>How long to wait for QEMU's first scanout when opening a display session.</summary>
    public TimeSpan DisplayReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
