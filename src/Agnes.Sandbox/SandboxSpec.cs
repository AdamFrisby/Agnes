namespace Agnes.Sandbox;

/// <summary>What to provision. Mirrors CodeyBox's SandboxSpec, trimmed to Agnes's needs.</summary>
public sealed record SandboxSpec
{
    /// <summary>Base image reference (e.g. "images:ubuntu/24.04/cloud"); empty = provider default.</summary>
    public string ImageReference { get; init; } = string.Empty;

    /// <summary>Working directory the agent operates in (inside the sandbox).</summary>
    public string WorkingDirectory { get; init; } = "/work";

    /// <summary>Absolute host path bind-mounted read/write to <see cref="WorkingDirectory"/>, if any.</summary>
    public string? HostWorkingDirectory { get; init; }

    /// <summary>Per-session resource overrides (RAM/disk/CPU). Null fields fall back to the provider's
    /// configured default; a null override means "all defaults". The provider resolves this against its
    /// <c>DefaultLimits</c> — nothing here is ever a full <see cref="SandboxResourceLimits"/> on its own.</summary>
    public SandboxResourceOverride? ResourceOverride { get; init; }

    /// <summary>Host bridge to attach the NIC to (provider default if null).</summary>
    public string? NetworkBridge { get; init; }
}

/// <summary>Resource caps for a sandbox VM. Defaults match CodeyBox (2 CPU / 12 GiB / 16 GiB).</summary>
public sealed record SandboxResourceLimits
{
    public int CpuCount { get; init; } = 2;
    public long MemoryBytes { get; init; } = 12L * 1024 * 1024 * 1024;
    public long DiskBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>Applies a partial override: each field the override specifies wins, the rest stay as configured.
    /// Returns <c>this</c> unchanged when the override is null or empty.</summary>
    public SandboxResourceLimits With(SandboxResourceOverride? o)
        => o is null ? this : this with
        {
            CpuCount = o.CpuCount ?? CpuCount,
            MemoryBytes = o.MemoryBytes ?? MemoryBytes,
            DiskBytes = o.DiskBytes ?? DiskBytes,
        };
}

/// <summary>A partial resource override where any unset (null) field inherits the provider's configured
/// default. Kept separate from <see cref="SandboxResourceLimits"/> (whose fields are always concrete) so a
/// caller can override just RAM without having to restate CPU and disk.</summary>
public sealed record SandboxResourceOverride
{
    public int? CpuCount { get; init; }
    public long? MemoryBytes { get; init; }
    public long? DiskBytes { get; init; }

    /// <summary>True when nothing is overridden (all fields null) — treated the same as no override at all.</summary>
    public bool IsEmpty => CpuCount is null && MemoryBytes is null && DiskBytes is null;
}

/// <summary>A one-shot command to run inside a sandbox (streaming output).</summary>
public sealed record SandboxExec
{
    public required IReadOnlyList<string> Argv { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Data piped to the command's stdin (used to carry credential payloads safely).</summary>
    public string? Stdin { get; init; }

    /// <summary>True when the environment/stdin carries secrets (keeps them off argv/logs).</summary>
    public bool EnvironmentContainsSecrets { get; init; }

    public Action<string>? StdoutChunkCallback { get; init; }
    public Action<string>? StderrChunkCallback { get; init; }
}

/// <summary>Result of a sandbox exec.</summary>
public sealed record SandboxExecResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
