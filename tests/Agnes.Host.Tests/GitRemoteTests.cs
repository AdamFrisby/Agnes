using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class GitRemoteTests
{
    [Theory]
    [InlineData("https://github.com/AdamFrisby/Agnes.git", "github.com", "AdamFrisby/Agnes")]
    [InlineData("https://github.com/AdamFrisby/Agnes", "github.com", "AdamFrisby/Agnes")]
    [InlineData("git@github.com:AdamFrisby/Agnes.git", "github.com", "AdamFrisby/Agnes")]
    [InlineData("ssh://git@github.com/AdamFrisby/Agnes.git", "github.com", "AdamFrisby/Agnes")]
    [InlineData("https://gitlab.com/group/sub/proj.git", "gitlab.com", "group/sub/proj")]
    public void Parses_host_and_repo_from_every_remote_form(string url, string expectedHost, string expectedRepo)
    {
        Assert.True(GitRemote.TryParse(url, out var host, out var repo));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedRepo, repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://github.com/")]        // no owner/repo
    [InlineData("https://github.com/justowner")] // no repo segment
    public void Rejects_anything_without_a_clear_host_and_repo(string? url)
        => Assert.False(GitRemote.TryParse(url, out _, out _));
}
