using System.Diagnostics;
using Agnes.Host.Git;

namespace Agnes.Host.Tests;

public class GitServiceTests
{
    private static async Task<int> RunGitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    [Fact]
    public async Task Reports_status_for_a_non_repo_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agnes-nogit-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        try
        {
            var status = await new GitService().GetStatusAsync(dir);
            Assert.False(status.IsRepository);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Reports_changes_and_commits_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agnes-git-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        try
        {
            await RunGitAsync(dir, "init");
            await RunGitAsync(dir, "config", "user.email", "t@example.com");
            await RunGitAsync(dir, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "hello");

            var git = new GitService();
            var before = await git.GetStatusAsync(dir);
            Assert.True(before.IsRepository);
            Assert.True(before.IsDirty);
            Assert.Contains(before.Changes, c => c.Path == "a.txt");

            var commit = await git.CommitAsync(dir, "add a.txt");
            Assert.True(commit.Success);

            var after = await git.GetStatusAsync(dir);
            Assert.False(after.IsDirty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Creates_and_removes_an_isolated_worktree()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"agnes-wt-{Guid.NewGuid():n}");
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            await RunGitAsync(repo, "init");
            await RunGitAsync(repo, "config", "user.email", "t@example.com");
            await RunGitAsync(repo, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "hello");
            await RunGitAsync(repo, "add", "-A");
            await RunGitAsync(repo, "commit", "-m", "init"); // worktree add needs a HEAD

            var git = new GitService();
            var worktree = await git.CreateWorktreeAsync(repo, "sess1");

            Assert.NotNull(worktree);
            Assert.True(Directory.Exists(worktree));
            Assert.True(File.Exists(Path.Combine(worktree!, "a.txt"))); // it's a real checkout

            await git.RemoveWorktreeAsync(repo, worktree!);
            Assert.False(Directory.Exists(worktree));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
