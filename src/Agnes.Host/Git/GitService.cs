using System.Diagnostics;
using Agnes.Protocol;

namespace Agnes.Host.Git;

/// <summary>
/// Reads and mutates git state by shelling out to the already-installed <c>git</c> in a session's
/// working directory. Stateless; safe to share. Never installs anything — it only runs git.
/// </summary>
public sealed class GitService
{
    private static readonly GitStatus NotARepo = new(false, null, false, []);

    public async Task<GitStatus> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return NotARepo;
        }

        var inside = await RunAsync(workingDirectory, cancellationToken, "rev-parse", "--is-inside-work-tree");
        if (inside.ExitCode != 0 || inside.StdOut.Trim() != "true")
        {
            return NotARepo;
        }

        var branch = (await RunAsync(workingDirectory, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD")).StdOut.Trim();
        var porcelain = await RunAsync(workingDirectory, cancellationToken, "status", "--porcelain");

        var changes = new List<GitFileChange>();
        foreach (var line in porcelain.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3)
            {
                continue;
            }

            var status = line[..2].Trim();
            var path = line[3..].Trim();
            changes.Add(new GitFileChange(path, string.IsNullOrEmpty(status) ? "?" : status));
        }

        return new GitStatus(true, string.IsNullOrEmpty(branch) ? null : branch, changes.Count > 0, changes);
    }

    public async Task<GitCommitResult> CommitAsync(string workingDirectory, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitCommitResult(false, "Empty commit message.");
        }

        var status = await GetStatusAsync(workingDirectory, cancellationToken);
        if (!status.IsRepository)
        {
            return new GitCommitResult(false, "Not a git repository.");
        }

        await RunAsync(workingDirectory, cancellationToken, "add", "-A");
        var commit = await RunAsync(workingDirectory, cancellationToken, "commit", "-m", message);
        var output = (commit.StdOut + commit.StdErr).Trim();
        return new GitCommitResult(commit.ExitCode == 0, output.Length > 0 ? output : (commit.ExitCode == 0 ? "Committed." : "Commit failed."));
    }

    /// <summary>
    /// Creates an isolated git worktree (on a new <c>agnes/&lt;name&gt;</c> branch) so a session can
    /// run in parallel without colliding with others. Returns the worktree path, or null if the
    /// directory isn't a git repo or the worktree couldn't be created.
    /// </summary>
    public async Task<string?> CreateWorktreeAsync(string repoDirectory, string name, CancellationToken cancellationToken = default)
    {
        var root = (await RunAsync(repoDirectory, cancellationToken, "rev-parse", "--show-toplevel")).StdOut.Trim();
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(root) ?? root;
        var worktreePath = Path.Combine(parent, ".agnes-worktrees", name);
        var result = await RunAsync(repoDirectory, cancellationToken, "worktree", "add", "-b", $"agnes/{name}", worktreePath);
        return result.ExitCode == 0 ? worktreePath : null;
    }

    public Task RemoveWorktreeAsync(string repoDirectory, string worktreePath, CancellationToken cancellationToken = default)
        => RunAsync(repoDirectory, cancellationToken, "worktree", "remove", "--force", worktreePath);

    /// <summary>The working directory's <c>origin</c> remote URL, or null if there's no repo/remote.</summary>
    public async Task<string?> GetRemoteUrlAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var result = await RunAsync(workingDirectory, cancellationToken, "remote", "get-url", "origin");
        return result.ExitCode == 0 && result.StdOut.Trim().Length > 0 ? result.StdOut.Trim() : null;
    }

    /// <summary>The commit identity git would use here (repo-local or global), for the sandbox's gitconfig.</summary>
    public async Task<(string? Name, string? Email)> GetIdentityAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return (null, null);
        }

        var name = (await RunAsync(workingDirectory, cancellationToken, "config", "user.name")).StdOut.Trim();
        var email = (await RunAsync(workingDirectory, cancellationToken, "config", "user.email")).StdOut.Trim();
        return (string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(email) ? null : email);
    }

    /// <summary>Whether <paramref name="directory"/> already contains a git checkout.</summary>
    public static bool IsGitRepo(string directory)
        => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(Path.Combine(directory, ".git"));

    /// <summary>Whether it's safe to clone into <paramref name="directory"/> (missing or empty).</summary>
    public static bool IsEmptyOrMissing(string directory)
        => string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any();

    /// <summary>
    /// Clones a repo into <paramref name="targetDirectory"/> (created if needed). The network operation
    /// uses <paramref name="authenticatedUrl"/> (which carries a short-lived token); afterwards the remote
    /// is reset to the clean <paramref name="cleanUrl"/> so no credential is left in <c>.git/config</c>.
    /// </summary>
    public async Task<(bool Ok, string Message)> CloneAsync(
        string cleanUrl, string authenticatedUrl, string targetDirectory, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(targetDirectory.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(parent))
        {
            return (false, "Invalid target directory.");
        }

        Directory.CreateDirectory(parent);
        var clone = await RunAsync(parent, cancellationToken, "clone", authenticatedUrl, targetDirectory);
        if (clone.ExitCode != 0)
        {
            // Redact the token from any echoed URL before surfacing the error.
            var msg = (clone.StdErr + clone.StdOut).Trim().Replace(authenticatedUrl, cleanUrl, StringComparison.Ordinal);
            return (false, msg.Length > 0 ? msg : "git clone failed.");
        }

        await RunAsync(targetDirectory, cancellationToken, "remote", "set-url", "origin", cleanUrl);
        return (true, "Cloned.");
    }

    /// <summary>
    /// Stashes every uncommitted change (including untracked files) so the working tree is left clean, and
    /// returns metadata identifying the stash (its commit sha, the branch, a timestamp, and how many files it
    /// captured). Returns null when there's nothing to stash or the directory isn't a repo.
    /// </summary>
    public async Task<GitStashInfo?> StashAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!status.IsRepository || !status.IsDirty)
        {
            return null;
        }

        ClearStaleIndexLock(workingDirectory);
        var branch = status.Branch ?? "(detached)";
        var fileCount = status.Changes.Count;

        var push = await RunAsync(workingDirectory, cancellationToken, "stash", "push", "--include-untracked");
        if (push.ExitCode != 0)
        {
            return null;
        }

        var sha = (await RunAsync(workingDirectory, cancellationToken, "rev-parse", "stash@{0}")).StdOut.Trim();
        return new GitStashInfo(sha.Length > 0 ? sha : "stash@{0}", branch, DateTimeOffset.UtcNow, fileCount);
    }

    /// <summary>Reapplies (and drops) a previously created stash, identified by the sha returned from
    /// <see cref="StashAsync"/>. On a reapply conflict the stash is left intact (git keeps it) — no data loss.</summary>
    public async Task<GitOperationResult> PopStashAsync(string workingDirectory, string stashId, CancellationToken cancellationToken = default)
    {
        var reference = await ResolveStashRefAsync(workingDirectory, stashId, cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            return new GitOperationResult(false, "Stash not found.");
        }

        ClearStaleIndexLock(workingDirectory);
        var pop = await RunAsync(workingDirectory, cancellationToken, "stash", "pop", reference);
        var msg = (pop.StdErr + pop.StdOut).Trim();
        return new GitOperationResult(pop.ExitCode == 0, msg.Length > 0 ? msg : (pop.ExitCode == 0 ? "Stash restored." : "Stash pop failed."));
    }

    /// <summary>
    /// Switches to <paramref name="branch"/>, optionally carrying uncommitted changes across via a stash
    /// (stash → switch → reapply). If the reapply conflicts, the switch stands but the changes are preserved
    /// in the stash (surfaced as <see cref="GitSwitchResult.StashReapplyConflict"/>) — never discarded. If the
    /// switch itself fails, a carried stash is popped back so the original state is restored.
    /// </summary>
    public async Task<GitSwitchResult> SwitchBranchAsync(string workingDirectory, string branch, bool carryStash, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!status.IsRepository)
        {
            return new GitSwitchResult(false, false, null, "Not a git repository.");
        }

        GitStashInfo? stash = null;
        if (carryStash && status.IsDirty)
        {
            stash = await StashAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            if (stash is null)
            {
                return new GitSwitchResult(false, false, null, "Could not stash changes before switching branches.");
            }
        }

        ClearStaleIndexLock(workingDirectory);
        var switched = await RunAsync(workingDirectory, cancellationToken, "switch", branch);
        if (switched.ExitCode != 0)
        {
            if (stash is not null)
            {
                // Roll back so no work is lost if the branch didn't exist / couldn't be entered.
                await RunAsync(workingDirectory, cancellationToken, "stash", "pop");
            }

            var err = (switched.StdErr + switched.StdOut).Trim();
            return new GitSwitchResult(false, false, null, err.Length > 0 ? err : "Branch switch failed.");
        }

        if (stash is not null)
        {
            var pop = await PopStashAsync(workingDirectory, stash.StashId, cancellationToken).ConfigureAwait(false);
            if (!pop.Success)
            {
                return new GitSwitchResult(false, true, stash.StashId,
                    $"Switched to {branch}, but the carried changes could not be reapplied cleanly and are preserved in stash {stash.StashId}. {pop.Message}");
            }
        }

        return new GitSwitchResult(true, false, null, $"Switched to {branch}.");
    }

    /// <summary>
    /// Pulls only if the update is a clean fast-forward (<c>git pull --ff-only</c>). A diverged remote is
    /// refused here, at the API layer, with a typed <see cref="GitPullResult.NonFastForward"/> error — git is
    /// never allowed to merge or rebase behind the user's back. This is a server-side safety rule, not a UI one.
    /// </summary>
    public async Task<GitPullResult> FastForwardPullAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (!(await GetStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false)).IsRepository)
        {
            return new GitPullResult(false, false, "Not a git repository.");
        }

        var pull = await RunAsync(workingDirectory, cancellationToken, "pull", "--ff-only");
        if (pull.ExitCode == 0)
        {
            var okMsg = (pull.StdOut + pull.StdErr).Trim();
            return new GitPullResult(true, false, okMsg.Length > 0 ? okMsg : "Already up to date.");
        }

        var text = (pull.StdErr + pull.StdOut).Trim();
        var nonFastForward = text.Contains("fast-forward", StringComparison.OrdinalIgnoreCase)
            || text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || text.Contains("diverg", StringComparison.OrdinalIgnoreCase);
        return new GitPullResult(false, nonFastForward, text.Length > 0 ? text : "Pull failed.");
    }

    /// <summary>Pushes the current branch. When <paramref name="publishBranch"/> is set, publishes it with an
    /// upstream (<c>push -u origin HEAD</c>) so a brand-new local branch appears on the remote.</summary>
    public async Task<GitOperationResult> PushAsync(string workingDirectory, bool publishBranch, CancellationToken cancellationToken = default)
    {
        if (!(await GetStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false)).IsRepository)
        {
            return new GitOperationResult(false, "Not a git repository.");
        }

        var push = publishBranch
            ? await RunAsync(workingDirectory, cancellationToken, "push", "-u", "origin", "HEAD")
            : await RunAsync(workingDirectory, cancellationToken, "push");
        var msg = (push.StdErr + push.StdOut).Trim();
        return new GitOperationResult(push.ExitCode == 0, msg.Length > 0 ? msg : (push.ExitCode == 0 ? "Pushed." : "Push failed."));
    }

    /// <summary>Fetches <paramref name="remoteRefspec"/> from <c>origin</c> into a local branch and checks it
    /// out. Used by forge providers to land a PR/MR into the session's working directory.</summary>
    public async Task<GitOperationResult> FetchAndCheckoutAsync(string workingDirectory, string remoteRefspec, string localBranch, CancellationToken cancellationToken = default)
    {
        if (!(await GetStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false)).IsRepository)
        {
            return new GitOperationResult(false, "Not a git repository.");
        }

        var fetch = await RunAsync(workingDirectory, cancellationToken, "fetch", "origin", remoteRefspec);
        if (fetch.ExitCode != 0)
        {
            var err = (fetch.StdErr + fetch.StdOut).Trim();
            return new GitOperationResult(false, err.Length > 0 ? err : "Fetch failed.");
        }

        var checkout = await RunAsync(workingDirectory, cancellationToken, "checkout", localBranch);
        var msg = (checkout.StdErr + checkout.StdOut).Trim();
        return new GitOperationResult(checkout.ExitCode == 0, msg.Length > 0 ? msg : (checkout.ExitCode == 0 ? $"Checked out {localBranch}." : "Checkout failed."));
    }

    /// <summary>Maps a stash commit sha back to its <c>stash@{n}</c> reference (pop/apply need the ref to also
    /// drop it). Returns the input unchanged if it already looks like a stash reference, or null if unknown.</summary>
    private async Task<string?> ResolveStashRefAsync(string workingDirectory, string stashId, CancellationToken cancellationToken)
    {
        if (stashId.StartsWith("stash@", StringComparison.Ordinal))
        {
            return stashId;
        }

        var list = await RunAsync(workingDirectory, cancellationToken, "stash", "list", "--format=%gd %H");
        foreach (var line in list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[1], stashId, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Removes a <em>stale</em> <c>.git/index.lock</c> only when it's safe: a crashed prior git can leave a
    /// lock that wedges every future operation, but blindly deleting a lock a live git still holds can corrupt
    /// the index. We therefore delete only when no <c>git</c> process is currently running on the machine.
    /// </summary>
    private static void ClearStaleIndexLock(string workingDirectory)
    {
        try
        {
            var lockPath = Path.Combine(workingDirectory, ".git", "index.lock");
            if (File.Exists(lockPath) && Process.GetProcessesByName("git").Length == 0)
            {
                File.Delete(lockPath);
            }
        }
        catch
        {
            // Best effort — if we can't verify or clear it, the git command below surfaces the lock error.
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string workingDirectory, CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "Could not start git.");
            }

            var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, stdOut, stdErr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
