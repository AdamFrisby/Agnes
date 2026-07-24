namespace Agnes.Host.Projects;

/// <summary>
/// A host's persisted record of one on-disk checkout of a workspace (multi-machine workspace model,
/// <c>connectivity/05</c>). <see cref="WorkspaceId"/> is derived from <see cref="RepositoryUrl"/> via
/// <see cref="Agnes.Abstractions.WorkspaceIdentity"/> at creation and stored, so a checkout is tagged with
/// its logical workspace even when nothing else on the host is. A worktree checkout also remembers the
/// clone it hangs off (<see cref="WorktreeParentPath"/>) so it can be removed cleanly with
/// <c>git worktree remove</c>. The current branch is not stored — it's read live from git.
/// </summary>
public sealed record CheckoutRecord(
    string Id,
    string WorkspaceId,
    string RepositoryUrl,
    string DisplayName,
    string Path,
    bool IsWorktree,
    string? WorktreeParentPath);
