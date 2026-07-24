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

    public SandboxResourceLimits Limits { get; init; } = new();

    /// <summary>Host bridge to attach the NIC to (provider default if null).</summary>
    public string? NetworkBridge { get; init; }

    /// <summary>Named network profile selecting the egress policy (resolved to a bridge by the provider's
    /// profile→bridge map). Null = the provider's default profile. Overridden by <see cref="NetworkBridge"/>.</summary>
    public string? NetworkProfile { get; init; }
}

/// <summary>Resource caps for a sandbox VM. Defaults match CodeyBox (2 CPU / 12 GiB / 16 GiB).</summary>
public sealed record SandboxResourceLimits
{
    public int CpuCount { get; init; } = 2;
    public long MemoryBytes { get; init; } = 12L * 1024 * 1024 * 1024;
    public long DiskBytes { get; init; } = 16L * 1024 * 1024 * 1024;
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
