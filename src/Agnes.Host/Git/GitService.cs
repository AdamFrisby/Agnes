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
