using Agnes.Abstractions;
using Agnes.Host.Projects;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Git;

/// <summary>
/// The host side of the multi-machine workspace model (<c>connectivity/05</c>): lifecycle for this host's
/// on-disk checkouts of workspaces. A checkout is created as a fresh clone or — when the host already has a
/// clone of the same repo and the caller wants a second working copy on the same machine — as a git worktree
/// of that clone; its branch can be switched; and it can be cleaned up, refusing to discard uncommitted work
/// unless forced. Every git operation reuses <see cref="GitService"/>'s deep-git primitives
/// (<see cref="GitService.CloneAsync"/>, <see cref="GitService.CreateWorktreeAsync"/>,
/// <see cref="GitService.SwitchBranchAsync"/>, <see cref="GitService.GetStatusAsync"/>) rather than reinventing
/// git handling; persistence goes through <see cref="CheckoutStore"/>. Stateless beyond the store; safe to share.
/// </summary>
public sealed class CheckoutManager
{
    private readonly GitService _git;
    private readonly CheckoutStore _store;
    private readonly ILogger<CheckoutManager>? _logger;

    public CheckoutManager(GitService git, CheckoutStore store, ILogger<CheckoutManager>? logger = null)
    {
        _git = git;
        _store = store;
        _logger = logger;
    }

    /// <summary>This host's checkouts, each with its branch read live from git.</summary>
    public async Task<IReadOnlyList<CheckoutDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = _store.List();
        var dtos = new List<CheckoutDto>(records.Count);
        foreach (var record in records)
        {
            var branch = await BranchOfAsync(record.Path, cancellationToken).ConfigureAwait(false);
            dtos.Add(ToDto(record, branch));
        }

        return dtos;
    }

    /// <summary>
    /// Creates a checkout of <paramref name="repositoryUrl"/>. When <paramref name="useWorktreeOfExisting"/> is
    /// set and this host already has a full-clone checkout of the same workspace, the new checkout is a git
    /// worktree of that clone (a second working copy on the same host, no re-clone); otherwise it's a fresh
    /// clone into <paramref name="path"/>. On a clone, an optional <paramref name="branch"/> is switched to
    /// afterward.
    /// </summary>
    public async Task<CheckoutOperationResult> CreateCheckoutAsync(
        string repositoryUrl, string path, string? branch = null, bool useWorktreeOfExisting = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return new CheckoutOperationResult(false, null, "A repository URL is required.");
        }

        var workspaceId = WorkspaceIdentity.Normalize(repositoryUrl);
        var displayName = WorkspaceIdentity.DisplayName(repositoryUrl);

        // A second working copy on the same host: prefer a worktree of an existing clone over a second clone.
        if (useWorktreeOfExisting)
        {
            var baseClone = _store.List().FirstOrDefault(c => c.WorkspaceId == workspaceId && !c.IsWorktree);
            if (baseClone is not null)
            {
                var name = WorktreeName(path);
                var worktreePath = await _git.CreateWorktreeAsync(baseClone.Path, name, cancellationToken).ConfigureAwait(false);
                if (worktreePath is null)
                {
                    return new CheckoutOperationResult(false, null, "Could not add a worktree of the existing clone.");
                }

                var wtRecord = new CheckoutRecord(NewId(), workspaceId, repositoryUrl, displayName, worktreePath, IsWorktree: true, baseClone.Path);
                _store.Save(wtRecord);
                _logger?.LogInformation("Added worktree checkout {Id} of {Workspace} at {Path}.", wtRecord.Id, workspaceId, worktreePath);
                var wtBranch = await BranchOfAsync(worktreePath, cancellationToken).ConfigureAwait(false);
                return new CheckoutOperationResult(true, ToDto(wtRecord, wtBranch), "Worktree added.");
            }

            // No existing clone to hang a worktree off — fall through to a normal clone.
        }

        // Fresh clone. No credential injection at this layer (that's the session-start path's concern); the
        // clean and authenticated URLs are the same here, which is exactly what an offline/local clone needs.
        var (ok, message) = await _git.CloneAsync(repositoryUrl, repositoryUrl, path, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return new CheckoutOperationResult(false, null, message);
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            await _git.SwitchBranchAsync(path, branch, carryStash: false, cancellationToken).ConfigureAwait(false);
        }

        var record = new CheckoutRecord(NewId(), workspaceId, repositoryUrl, displayName, path, IsWorktree: false, WorktreeParentPath: null);
        _store.Save(record);
        _logger?.LogInformation("Created clone checkout {Id} of {Workspace} at {Path}.", record.Id, workspaceId, path);
        var createdBranch = await BranchOfAsync(path, cancellationToken).ConfigureAwait(false);
        return new CheckoutOperationResult(true, ToDto(record, createdBranch), message);
    }

    /// <summary>Switches a checkout's branch, carrying uncommitted work across as a stash (reuses
    /// <see cref="GitService.SwitchBranchAsync"/>).</summary>
    public async Task<GitSwitchResult> SwitchCheckoutBranchAsync(string checkoutId, string branch, CancellationToken cancellationToken = default)
    {
        var record = _store.Get(checkoutId);
        if (record is null)
        {
            return new GitSwitchResult(false, false, null, $"No such checkout '{checkoutId}'.");
        }

        return await _git.SwitchBranchAsync(record.Path, branch, carryStash: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a checkout. When it has uncommitted work and <paramref name="force"/> is false, it refuses with a
    /// message naming the dirty files — no data loss. A worktree is removed with <c>git worktree remove</c> off
    /// its parent clone; a full clone's directory is deleted. The persisted record is dropped either way.
    /// </summary>
    public async Task<CheckoutOperationResult> CleanUpCheckoutAsync(string checkoutId, bool force, CancellationToken cancellationToken = default)
    {
        var record = _store.Get(checkoutId);
        if (record is null)
        {
            return new CheckoutOperationResult(false, null, $"No such checkout '{checkoutId}'.");
        }

        var status = await _git.GetStatusAsync(record.Path, cancellationToken).ConfigureAwait(false);
        if (!force && status.IsRepository && status.IsDirty)
        {
            var files = string.Join(", ", status.Changes.Select(c => c.Path));
            return new CheckoutOperationResult(false, ToDto(record, status.Branch),
                $"Checkout '{record.DisplayName}' has {status.Changes.Count} uncommitted change(s) ({files}). " +
                "Commit or stash them, or clean up with force to remove anyway.");
        }

        try
        {
            if (record is { IsWorktree: true, WorktreeParentPath: { } parent })
            {
                await _git.RemoveWorktreeAsync(parent, record.Path, cancellationToken).ConfigureAwait(false);
            }
            else if (Directory.Exists(record.Path))
            {
                Directory.Delete(record.Path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove checkout {Id} at {Path}.", record.Id, record.Path);
            return new CheckoutOperationResult(false, ToDto(record, status.Branch), $"Could not remove the checkout: {ex.Message}");
        }

        _store.Remove(checkoutId);
        _logger?.LogInformation("Cleaned up checkout {Id} at {Path}.", record.Id, record.Path);
        return new CheckoutOperationResult(true, null, "Checkout removed.");
    }

    private async Task<string?> BranchOfAsync(string path, CancellationToken cancellationToken)
    {
        var status = await _git.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
        return status.IsRepository ? status.Branch : null;
    }

    private static CheckoutDto ToDto(CheckoutRecord record, string? branch)
        => new(record.Id, record.WorkspaceId, record.RepositoryUrl, record.DisplayName, record.Path, branch, record.IsWorktree);

    // A stable-ish worktree name from the requested path's leaf (git makes the branch agnes/<name>), or a guid.
    private static string WorktreeName(string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(leaf) ? Guid.NewGuid().ToString("n")[..8] : leaf;
    }

    private static string NewId() => Guid.NewGuid().ToString("n");
}
