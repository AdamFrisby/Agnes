using Agnes.Host.Hosting;
using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Tests;

/// <summary>
/// Covers High-3 credential scoping: a session pinned to a linked account (its project's CredentialAccount)
/// can only mint against that account — a pinned-but-unlinked account is refused rather than silently falling
/// back to another tenant's account.
/// </summary>
public class CredentialScopingTests
{
    [Fact]
    public async Task Pinned_account_that_is_not_linked_is_refused_not_substituted()
    {
        var accounts = new List<GitHubAppConfig> { new("1", "slug", InstallationId: 1, "pem", Account: "acme") };
        var source = new GitHubAppCredentialSource(() => accounts, new HttpClient());

        // The request is pinned to an account that isn't linked: refuse (null), never fall back to "acme".
        // Returns before any network call, so this is a genuine offline unit test of the selection rule.
        var cred = await source.ResolveAsync(new CredentialRequest("https", "github.com", "acme/repo", "get", Account: "other-org"));

        Assert.Null(cred);
    }

    [Fact]
    public void Grant_and_request_carry_the_pinned_account()
    {
        var grant = new CredentialGrant("s", "github.com", "*", "Ask", Account: "acme");
        Assert.Equal("acme", grant.Account);

        var request = new CredentialRequest("https", "github.com", "acme/repo", "get", Account: "acme");
        Assert.Equal("acme", request.Account);
    }
}
