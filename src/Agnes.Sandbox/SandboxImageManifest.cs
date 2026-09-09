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
        // Self-contained ELFs copied from the host (fast, no network). An agent whose binary isn't on
        // the host PATH is skipped by the bake with a progress note, so listing one costs nothing on a
        // host that lacks it. The claude-code-acp bridge stays out: it's node-based and its package
        // name varies, so the user adds it as an NpmGlobal.
        new("claude-code-native", "copy:claude"),
        new("codex", "copy:codex"),
        new("opencode", "copy:opencode"),
    ];

    /// <summary>
    /// The packages a guest needs to put something on a screen: an X server on the virtual GPU, a
    /// window manager, a terminal, and fonts. Listed here rather than in a provider because a graphical
    /// tier is a property of the *image*, and any provider that boots a Linux guest needs the same set.
    /// </summary>
    /// <remarks>
    /// <c>xserver-xorg-core</c> carries the <c>modesetting</c> driver, which is what drives virtio-gpu;
    /// no vendor DDX is wanted. <c>x11-xserver-utils</c> is <c>xrandr</c> and <c>xsetroot</c>, used once
    /// at session start to force the mode. Nothing here captures or serves anything: the screen is read
    /// from outside the guest (see <see cref="IDisplaySource"/>), which is the whole point.
    /// </remarks>
    public static IReadOnlyList<string> GraphicalAptPackages =>
    [
        "xserver-xorg-core",
        "x11-xserver-utils",
        "xinit",
        "openbox",
        "xterm",
        "fonts-dejavu-core",
        "dbus-x11",
        "x11-utils",
    ];

    /// <summary>
    /// The graphical tier of a baseline manifest: the same image plus a desktop, under its own alias so
    /// a host that never asks for a display never pays for one. The X server's *configuration* is not
    /// baked in — it is written per session by cloud-init, because it carries the session's resolution.
    /// </summary>
    public SandboxImageManifest AsGraphical(string alias = "agnes-graphical")
        => this with { Alias = alias, AptPackages = [.. AptPackages, .. GraphicalAptPackages] };

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
