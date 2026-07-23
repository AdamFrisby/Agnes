using System.Diagnostics;
using Agnes.Host.Git;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// End-to-end coverage for the deep-git-integration operations (stash/pop, carry-stash branch switch,
/// fast-forward-only pull) against real temporary git repositories, plus the GitHub forge provider's URL/
/// branch construction and PR parsing (no network — the REST call is stubbed).
/// </summary>
public sealed class DeepGitIntegrationTests
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

    private static async Task InitRepoAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await GitAsync(dir, "init", "-b", "main");
        await GitAsync(dir, "config", "user.email", "t@example.com");
        await GitAsync(dir, "config", "user.name", "Test");
        await GitAsync(dir, "config", "commit.gpgsign", "false");
    }

    private static string TempDir(string tag) => Path.Combine(Path.GetTempPath(), $"agnes-{tag}-{Guid.NewGuid():n}");

    [Fact]
    public async Task Stash_clears_the_tree_and_lists_metadata_then_pop_restores()
    {
        var dir = TempDir("stash");
        await InitRepoAsync(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "one");
            await GitAsync(dir, "add", "-A");
            await GitAsync(dir, "commit", "-m", "init");

            // Two dirty files (a modification + a new untracked file).
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "two");
            await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "new");

            var git = new GitService();
            var stash = await git.StashAsync(dir);

            Assert.NotNull(stash);
            Assert.Equal("main", stash!.Branch);
            Assert.Equal(2, stash.FileCount);
            Assert.NotEmpty(stash.StashId);

            // Working tree is clean and reverted to the committed content.
            var afterStash = await git.GetStatusAsync(dir);
            Assert.False(afterStash.IsDirty);
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(dir, "a.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "b.txt")));

            var pop = await git.PopStashAsync(dir, stash.StashId);
            Assert.True(pop.Success, pop.Message);

            // Exactly the original dirty state is back.
            Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(dir, "a.txt")));
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(dir, "b.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Stash_returns_null_when_the_tree_is_clean()
    {
        var dir = TempDir("stash-clean");
        await InitRepoAsync(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "one");
            await GitAsync(dir, "add", "-A");
            await GitAsync(dir, "commit", "-m", "init");

            Assert.Null(await new GitService().StashAsync(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Switch_with_carry_stash_moves_uncommitted_changes_to_the_new_branch()
    {
        var dir = TempDir("switch");
        await InitRepoAsync(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "base");
            await GitAsync(dir, "add", "-A");
            await GitAsync(dir, "commit", "-m", "init");
            await GitAsync(dir, "branch", "feature");

            // Uncommitted work on main.
            await File.WriteAllTextAsync(Path.Combine(dir, "work.txt"), "carried");

            var git = new GitService();
            var result = await git.SwitchBranchAsync(dir, "feature", carryStash: true);

            Assert.True(result.Success, result.Message);
            Assert.False(result.StashReapplyConflict);

            var status = await git.GetStatusAsync(dir);
            Assert.Equal("feature", status.Branch);
            Assert.True(status.IsDirty);
            Assert.Equal("carried", await File.ReadAllTextAsync(Path.Combine(dir, "work.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Switch_with_carry_stash_fails_cleanly_and_preserves_the_stash_on_conflict()
    {
        var dir = TempDir("switch-conflict");
        await InitRepoAsync(dir);
        try
        {
            var file = Path.Combine(dir, "a.txt");
            await File.WriteAllTextAsync(file, "base\n");
            await GitAsync(dir, "add", "-A");
            await GitAsync(dir, "commit", "-m", "init");

            // 'other' branch commits a conflicting version of the same file.
            await GitAsync(dir, "checkout", "-b", "other");
            await File.WriteAllTextAsync(file, "other-branch\n");
            await GitAsync(dir, "add", "-A");
            await GitAsync(dir, "commit", "-m", "other");
            await GitAsync(dir, "checkout", "main");

            // Uncommitted edit to the same file on main — reapplying it onto 'other' will conflict.
            await File.WriteAllTextAsync(file, "my-uncommitted\n");

            var git = new GitService();
            var result = await git.SwitchBranchAsync(dir, "other", carryStash: true);

            Assert.False(result.Success);
            Assert.True(result.StashReapplyConflict);
            Assert.NotNull(result.StashId);

            // No data loss: the stash is still present.
            var (_, stashList) = await GitAsync(dir, "stash", "list");
            Assert.False(string.IsNullOrWhiteSpace(stashList));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Fast_forward_pull_refuses_a_diverged_remote_and_makes_no_merge_commit()
    {
        var root = TempDir("ffpull");
        var origin = Path.Combine(root, "origin.git");
        var cloneA = Path.Combine(root, "a");
        var cloneB = Path.Combine(root, "b");
        Directory.CreateDirectory(root);
        try
        {
            // A bare origin seeded from clone A.
            Directory.CreateDirectory(origin);
            await GitAsync(origin, "init", "--bare", "-b", "main");

            await InitRepoAsync(cloneA);
            await GitAsync(cloneA, "remote", "add", "origin", origin);
            await File.WriteAllTextAsync(Path.Combine(cloneA, "a.txt"), "base");
            await GitAsync(cloneA, "add", "-A");
            await GitAsync(cloneA, "commit", "-m", "base");
            await GitAsync(cloneA, "push", "-u", "origin", "main");

            // Clone B off origin at the base commit.
            await GitAsync(root, "clone", origin, cloneB);
            await GitAsync(cloneB, "config", "user.email", "t@example.com");
            await GitAsync(cloneB, "config", "user.name", "Test");
            await GitAsync(cloneB, "config", "commit.gpgsign", "false");

            // A advances origin.
            await File.WriteAllTextAsync(Path.Combine(cloneA, "a.txt"), "advanced");
            await GitAsync(cloneA, "commit", "-am", "advance");
            await GitAsync(cloneA, "push", "origin", "main");

            // B diverges locally (a different commit on top of the same base).
            await File.WriteAllTextAsync(Path.Combine(cloneB, "b.txt"), "local");
            await GitAsync(cloneB, "add", "-A");
            await GitAsync(cloneB, "commit", "-m", "local-divergence");

            var (_, countBefore) = await GitAsync(cloneB, "rev-list", "--count", "HEAD");

            var result = await new GitService().FastForwardPullAsync(cloneB);

            Assert.False(result.Success);
            Assert.True(result.NonFastForward);

            // No merge/rebase happened: B's commit count is unchanged (no new merge commit).
            var (_, countAfter) = await GitAsync(cloneB, "rev-list", "--count", "HEAD");
            Assert.Equal(countBefore.Trim(), countAfter.Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fast_forward_pull_succeeds_when_the_remote_is_strictly_ahead()
    {
        var root = TempDir("ffpull-ok");
        var origin = Path.Combine(root, "origin.git");
        var cloneA = Path.Combine(root, "a");
        var cloneB = Path.Combine(root, "b");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(origin);
            await GitAsync(origin, "init", "--bare", "-b", "main");

            await InitRepoAsync(cloneA);
            await GitAsync(cloneA, "remote", "add", "origin", origin);
            await File.WriteAllTextAsync(Path.Combine(cloneA, "a.txt"), "base");
            await GitAsync(cloneA, "add", "-A");
            await GitAsync(cloneA, "commit", "-m", "base");
            await GitAsync(cloneA, "push", "-u", "origin", "main");

            await GitAsync(root, "clone", origin, cloneB);

            await File.WriteAllTextAsync(Path.Combine(cloneA, "a.txt"), "advanced");
            await GitAsync(cloneA, "commit", "-am", "advance");
            await GitAsync(cloneA, "push", "origin", "main");

            var result = await new GitService().FastForwardPullAsync(cloneB);

            Assert.True(result.Success, result.Message);
            Assert.False(result.NonFastForward);
            Assert.Equal("advanced", await File.ReadAllTextAsync(Path.Combine(cloneB, "a.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GitHub_provider_matches_github_and_ghe_hosts_but_not_others()
    {
        var github = new GitHubGitHostProvider();

        Assert.True(github.Matches("github.com"));
        Assert.True(github.Matches("GITHUB.COM"));
        Assert.True(github.Matches("myenterprise.github.com")); // GHE-style subdomain
        Assert.False(github.Matches("gitlab.com"));
        Assert.False(github.Matches("bitbucket.org"));
        Assert.False(github.Matches("notgithub.com"));
    }

    [Fact]
    public void GitHub_provider_builds_api_url_and_pr_branch_names()
    {
        Assert.Equal(
            "https://api.github.com/repos/acme/app/pulls?state=open",
            GitHubGitHostProvider.PullsApiUrl("github.com", "acme/app"));

        Assert.Equal(
            "https://ghe.acme.com/api/v3/repos/acme/app/pulls?state=open",
            GitHubGitHostProvider.PullsApiUrl("ghe.acme.com", "acme/app"));

        Assert.Equal("pr-42", GitHubGitHostProvider.LocalBranch("42"));
        Assert.Equal("pull/42/head:pr-42", GitHubGitHostProvider.RemoteRefspec("42"));
    }

    [Fact]
    public void GitHub_provider_parses_the_pulls_payload()
    {
        const string json = """
            [
              { "number": 7, "title": "Add feature", "html_url": "https://github.com/acme/app/pull/7",
                "head": { "ref": "feature/x" }, "user": { "login": "alice" } },
              { "number": 9, "title": "Fix bug", "html_url": "https://github.com/acme/app/pull/9",
                "head": { "ref": "bugfix" }, "user": { "login": "bob" } }
            ]
            """;

        var prs = GitHubGitHostProvider.ParsePullRequests(json);

        Assert.Equal(2, prs.Count);
        Assert.Equal("7", prs[0].Id);
        Assert.Equal("Add feature", prs[0].Title);
        Assert.Equal("feature/x", prs[0].SourceBranch);
        Assert.Equal("https://github.com/acme/app/pull/7", prs[0].Url);
        Assert.Equal("alice", prs[0].Author);
        Assert.Equal("bugfix", prs[1].SourceBranch);
    }

    [Fact]
    public async Task GitHub_provider_lists_prs_through_a_stubbed_handler_without_network()
    {
        const string json = """
            [ { "number": 3, "title": "Stubbed PR", "html_url": "https://github.com/acme/app/pull/3",
                "head": { "ref": "topic" }, "user": { "login": "carol" } } ]
            """;

        Uri? requested = null;
        var handler = new StubHandler((req) =>
        {
            requested = req.RequestUri;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        });

        var provider = new GitHubGitHostProvider(new HttpClient(handler));
        var prs = await provider.ListOpenPullRequestsAsync("git@github.com:acme/app.git");

        Assert.Equal("https://api.github.com/repos/acme/app/pulls?state=open", requested?.ToString());
        var pr = Assert.Single(prs);
        Assert.Equal("3", pr.Id);
        Assert.Equal("topic", pr.SourceBranch);
        Assert.Equal("carol", pr.Author);
    }

    [Fact]
    public async Task GitHub_provider_returns_empty_for_a_non_github_remote()
    {
        var provider = new GitHubGitHostProvider(new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("should not be called for a non-GitHub remote"))));

        Assert.Empty(await provider.ListOpenPullRequestsAsync("https://gitlab.com/acme/app.git"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
