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
}
