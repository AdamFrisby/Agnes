using Agnes.Protocol;
using Agnes.Sandbox;

namespace Agnes.Host.Projects;

/// <summary>The defaults a project suggests for a new session (the user can still override at open time).</summary>
public sealed record ProjectDefaults(bool SkipPermissions = false, string GitCredentialMode = "Ask", string McpApproval = "Ask");

/// <summary>
/// A project: the host-side bundle of everything that shapes a session — its sandbox contents, MCP
/// servers, GitHub account and defaults — keyed by the repository it targets. A session resolves its
/// project from the working directory's git remote (auto-created the first time it's seen, then
/// editable), so two projects (e.g. a work Rust repo and a personal .NET repo) can differ entirely on
/// the same host. The one project with an empty <see cref="RepoKey"/> is the default/fallback.
/// </summary>
public sealed record Project
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    public string Name { get; init; } = string.Empty;

    /// <summary>Detection key: "host/owner/repo" (e.g. "github.com/AdamFrisby/Agnes"), or "" for the default project.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>What this project's sandbox VM contains (base image, packages, agents).</summary>
    public SandboxImageManifest Sandbox { get; init; } = new();

    /// <summary>The MCP servers available to this project's sessions.</summary>
    public IReadOnlyList<McpServerInfo> McpServers { get; init; } = [];

    /// <summary>The linked GitHub account this project pushes as (null = the host's default account).</summary>
    public string? CredentialAccount { get; init; }

    /// <summary>The repository to check out for this project's sessions — a clone URL or "owner/repo".
    /// When set, opening a session auto-clones it into the working directory before the agent launches;
    /// null = no auto-checkout (the session uses whatever the working directory already contains).</summary>
    public string? Repo { get; init; }

    public ProjectDefaults Defaults { get; init; } = new();

    /// <summary>The catch-all project used when a session's working directory has no matching repo.</summary>
    public bool IsDefault => RepoKey.Length == 0;
}
