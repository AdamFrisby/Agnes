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

    /// <summary>Default resource caps (RAM/disk/CPU) for a session's sandbox VM, overridable per session via
    /// <see cref="Agnes.Sandbox.SandboxSpec.ResourceOverride"/>. Bound from host config; unset = 2 CPU / 12 GiB
    /// RAM / 16 GiB disk.</summary>
    public Agnes.Sandbox.SandboxResourceLimits DefaultLimits { get; init; } = new();

    /// <summary>Unprivileged uid/gid the agent runs as inside the guest.</summary>
    public int GuestUserId { get; init; } = 1000;
    public int GuestGroupId { get; init; } = 1000;
    public string GuestHome { get; init; } = "/home/agnes";

    public TimeSpan GuestReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan VmStopTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
