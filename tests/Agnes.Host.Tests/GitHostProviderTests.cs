using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>Git forges are built-in <see cref="IGitHostProvider"/> plugins in a registry (AC13), so a
/// remote host resolves to a forge through the registry rather than a hardcoded "github" check.</summary>
public class GitHostProviderTests
{
    [Fact]
    public void GitHub_provider_matches_github_hosts_only()
    {
        var github = new GitHubGitHostProvider();
        Assert.True(github.Matches("github.com"));
        Assert.True(github.Matches("GitHub.com"));
        Assert.True(github.Matches("myorg.github.com"));
        Assert.False(github.Matches("gitlab.com"));
        Assert.False(github.Matches("bitbucket.org"));
    }

    [Fact]
    public void Registry_resolves_a_remote_host_to_its_forge()
    {
        IReadOnlyList<IGitHostProvider> providers = [new GitHubGitHostProvider()];
        var registry = new PluginRegistry<IGitHostProvider>(providers, p => p.Id);

        Assert.Equal("github", registry.Find("github")!.Id);

        var forgeForRemote = GitRemote.TryParse("git@github.com:acme/app.git", out var host, out _)
            ? registry.All.FirstOrDefault(p => p.Matches(host))
            : null;
        Assert.NotNull(forgeForRemote);
        Assert.Equal("github", forgeForRemote!.Id);

        var unknownForge = GitRemote.TryParse("https://gitlab.com/acme/app.git", out var host2, out _)
            ? registry.All.FirstOrDefault(p => p.Matches(host2))
            : null;
        Assert.Null(unknownForge); // GitLab isn't a registered forge yet — a plugin would add it
    }
}
