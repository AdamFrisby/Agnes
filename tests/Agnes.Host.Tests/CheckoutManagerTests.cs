using System.Diagnostics;
using Agnes.Abstractions;
using Agnes.Host.Git;
using Agnes.Host.Projects;

namespace Agnes.Host.Tests;

/// <summary>
/// The host side of the multi-machine workspace model (connectivity/05): checkout lifecycle against real
/// temporary git repositories — a clone checkout, a second same-host checkout created as a worktree of it,
/// branch switching (reusing the deep-git primitive), and clean-up that refuses to discard uncommitted work
/// unless forced. Plus the checkout store's round-trip with its derived WorkspaceId.
/// </summary>
public sealed class CheckoutManagerTests
{
    private static async Task<(int ExitCode, string StdOut)> GitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout);
    }

    // A bare "origin" repo with one commit on main, that checkouts can clone from — all under a local temp dir
    // so the test is fully offline (no network).
    private static async Task<string> InitOriginAsync(string root, string branchB = "feature")
    {
        var origin = Path.Combine(root, "origin.git");
        var seed = Path.Combine(root, "seed");
        Directory.CreateDirectory(origin);
        await GitAsync(origin, "init", "--bare", "-b", "main");

        Directory.CreateDirectory(seed);
        await GitAsync(seed, "init", "-b", "main");
        await GitAsync(seed, "config", "user.email", "t@example.com");
        await GitAsync(seed, "config", "user.name", "Test");
        await GitAsync(seed, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(seed, "a.txt"), "base");
        await GitAsync(seed, "add", "-A");
        await GitAsync(seed, "commit", "-m", "base");
        await GitAsync(seed, "branch", branchB);
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        await GitAsync(seed, "push", "origin", branchB);
        return origin;
    }

    private static string TempDir(string tag) => Path.Combine(Path.GetTempPath(), $"agnes-{tag}-{Guid.NewGuid():n}");

    private static CheckoutManager NewManager(string root, out CheckoutStore store)
    {
        store = new CheckoutStore(Path.Combine(root, "checkouts.json"));
        return new CheckoutManager(new GitService(), store);
    }

    [Fact]
    public async Task Create_clone_checkout_then_a_second_same_host_checkout_is_a_worktree_of_it()
    {
        var root = TempDir("checkout-worktree");
        Directory.CreateDirectory(root);
        try
        {
            var origin = await InitOriginAsync(root);
            var manager = NewManager(root, out var store);

            // First checkout: a real clone.
            var cloneDir = Path.Combine(root, "clone");
            var first = await manager.CreateCheckoutAsync(origin, cloneDir);
            Assert.True(first.Success, first.Message);
            Assert.NotNull(first.Checkout);
            Assert.False(first.Checkout!.IsWorktree);
            Assert.True(GitService.IsGitRepo(cloneDir));

            // Second checkout of the SAME repo on the SAME host, requested as a worktree of the existing clone.
            var second = await manager.CreateCheckoutAsync(origin, Path.Combine(root, "second"), useWorktreeOfExisting: true);
            Assert.True(second.Success, second.Message);
            Assert.NotNull(second.Checkout);
            Assert.True(second.Checkout!.IsWorktree);

            // It's a real, distinct working tree that git recognizes as a worktree of the clone.
            Assert.True(Directory.Exists(second.Checkout.Path));
            Assert.NotEqual(cloneDir, second.Checkout.Path);
            var (_, worktrees) = await GitAsync(cloneDir, "worktree", "list");
            Assert.Contains(second.Checkout.Path, worktrees);

            // Both share one workspace id derived from the repo url; the store holds exactly the two.
            Assert.Equal(first.Checkout.WorkspaceId, second.Checkout.WorkspaceId);
            Assert.Equal(2, store.List().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Switch_checkout_branch_reuses_the_deep_git_primitive()
    {
        var root = TempDir("checkout-switch");
        Directory.CreateDirectory(root);
        try
        {
            var origin = await InitOriginAsync(root);
            var manager = NewManager(root, out _);

            var cloneDir = Path.Combine(root, "clone");
            var created = await manager.CreateCheckoutAsync(origin, cloneDir);
            Assert.True(created.Success, created.Message);
            Assert.Equal("main", created.Checkout!.Branch);

            var result = await manager.SwitchCheckoutBranchAsync(created.Checkout.Id, "feature");
            Assert.True(result.Success, result.Message);

            var status = await new GitService().GetStatusAsync(cloneDir);
            Assert.Equal("feature", status.Branch);

            // And it shows through the manager's live-branch listing.
            var listed = await manager.ListAsync();
            Assert.Equal("feature", Assert.Single(listed).Branch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Clean_up_refuses_uncommitted_work_without_force_and_succeeds_with_it()
    {
        var root = TempDir("checkout-cleanup-dirty");
        Directory.CreateDirectory(root);
        try
        {
            var origin = await InitOriginAsync(root);
            var manager = NewManager(root, out var store);

            var cloneDir = Path.Combine(root, "clone");
            var created = await manager.CreateCheckoutAsync(origin, cloneDir);
            Assert.True(created.Success, created.Message);

            // Dirty the working tree.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "a.txt"), "uncommitted change");
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "new.txt"), "new file");

            // Without force: refused, the message names the uncommitted work, and nothing is deleted.
            var refused = await manager.CleanUpCheckoutAsync(created.Checkout!.Id, force: false);
            Assert.False(refused.Success);
            Assert.Contains("uncommitted", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("a.txt", refused.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(cloneDir));
            Assert.NotNull(store.Get(created.Checkout.Id));

            // With force: removed from disk and from the store.
            var forced = await manager.CleanUpCheckoutAsync(created.Checkout.Id, force: true);
            Assert.True(forced.Success, forced.Message);
            Assert.False(Directory.Exists(cloneDir));
            Assert.Null(store.Get(created.Checkout.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Clean_up_of_a_clean_checkout_succeeds_without_force()
    {
        var root = TempDir("checkout-cleanup-clean");
        Directory.CreateDirectory(root);
        try
        {
            var origin = await InitOriginAsync(root);
            var manager = NewManager(root, out var store);

            var cloneDir = Path.Combine(root, "clone");
            var created = await manager.CreateCheckoutAsync(origin, cloneDir);
            Assert.True(created.Success, created.Message);

            var result = await manager.CleanUpCheckoutAsync(created.Checkout!.Id, force: false);
            Assert.True(result.Success, result.Message);
            Assert.False(Directory.Exists(cloneDir));
            Assert.Empty(store.List());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_round_trips_checkouts_tagged_with_the_workspace_id_derived_from_the_repo_url()
    {
        var root = TempDir("checkout-store");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "checkouts.json");
            const string repoUrl = "https://github.com/AdamFrisby/Agnes.git";
            var workspaceId = WorkspaceIdentity.Normalize(repoUrl);

            var store = new CheckoutStore(path);
            var record = new CheckoutRecord("c1", workspaceId, repoUrl, WorkspaceIdentity.DisplayName(repoUrl),
                Path.Combine(root, "wd"), IsWorktree: false, WorktreeParentPath: null);
            store.Save(record);

            // A fresh store instance loads it back with the WorkspaceId intact.
            var reloaded = new CheckoutStore(path);
            var loaded = Assert.Single(reloaded.List());
            Assert.Equal("c1", loaded.Id);
            Assert.Equal(workspaceId, loaded.WorkspaceId);
            Assert.Equal("github.com/adamfrisby/agnes", loaded.WorkspaceId);
            Assert.Equal(repoUrl, loaded.RepositoryUrl);

            Assert.True(reloaded.Remove("c1"));
            Assert.Empty(new CheckoutStore(path).List());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
