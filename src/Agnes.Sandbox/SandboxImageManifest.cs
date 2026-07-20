namespace Agnes.Sandbox;

/// <summary>
/// Declares what a baked sandbox image contains: a base image plus packages and agent CLIs to
/// preinstall. The host bakes this once into <see cref="Alias"/>; per-session VMs launch from it, so
/// they start complete (node, tools, agents) instead of a bare python3 cloud image.
/// </summary>
public sealed record SandboxImageManifest
{
    /// <summary>The upstream image the bake starts from.</summary>
    public string BaseImage { get; init; } = "images:ubuntu/24.04/cloud";

    /// <summary>The alias the baked image is published under (and that sessions launch from).</summary>
    public string Alias { get; init; } = "agnes-baseline";

    /// <summary>Install node + npm (unblocks npx MCP servers and node-based agents/bridges).</summary>
    public bool Node { get; init; } = true;

    /// <summary>apt packages to install.</summary>
    public IReadOnlyList<string> AptPackages { get; init; } =
        ["git", "ripgrep", "curl", "ca-certificates", "build-essential"];

    /// <summary>npm packages to install globally (e.g. node-based MCP servers).</summary>
    public IReadOnlyList<string> NpmGlobals { get; init; } = [];

    /// <summary>pip packages to install (system-wide, via pip --break-system-packages).</summary>
    public IReadOnlyList<string> PipPackages { get; init; } = [];

    /// <summary>Agent CLIs to bake in.</summary>
    public IReadOnlyList<SandboxImageAgent> Agents { get; init; } =
    [
        // Self-contained ELFs copied from the host (fast, no network). node-based agents (opencode,
        // the claude-code-acp bridge) are added by the user as NpmGlobals — packaging varies, so we
        // don't guess names as defaults.
        new("claude-code-native", "copy:claude"),
        new("codex", "copy:codex"),
    ];

    /// <summary>A short, stable fingerprint of the manifest — changes when a rebuild is warranted.</summary>
    public string Fingerprint()
    {
        var parts = new List<string> { BaseImage, Alias, Node ? "node" : "no-node" };
        parts.AddRange(AptPackages);
        parts.AddRange(NpmGlobals.Select(n => "npm:" + n));
        parts.AddRange(PipPackages.Select(p => "pip:" + p));
        parts.AddRange(Agents.Select(a => $"{a.AdapterId}={a.Source}"));
        return string.Join("|", parts);
    }
}

/// <summary>An agent CLI in a baked image. <see cref="Source"/> is "copy:&lt;hostBinary&gt;" or "npm:&lt;package&gt;".</summary>
public sealed record SandboxImageAgent(string AdapterId, string Source);
