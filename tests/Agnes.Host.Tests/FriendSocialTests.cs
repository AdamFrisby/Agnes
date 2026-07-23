using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Host.Social;

namespace Agnes.Host.Tests;

/// <summary>
/// Covers the collaboration/01 friends-and-social core under the maintainer's GitHub-identity model: a friend
/// is a GitHub-verified user; the social graph is shared GitHub org/team membership plus explicit add-by-handle;
/// every access grant is explicit and revocable; there is no ambient trust. The GitHub API is stubbed by a fake
/// <see cref="IGitHubUserLookup"/> (no network), and eligibility/identity are proven to be recomputed live.
/// </summary>
public class FriendSocialTests
{
    /// <summary>In-memory stand-in for the GitHub API. Every set is a knob the test flips to prove a decision is
    /// recomputed live rather than cached as trust.</summary>
    private sealed class FakeGitHub : IGitHubUserLookup
    {
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Org, string Login)> OrgMembers { get; } = new();
        public HashSet<(string Org, string Team, string Login)> TeamMembers { get; } = new();

        // Token-based paths (security/02 auth) are unused by the friends feature; default them away.
        public Task<string?> GetLoginAsync(string token, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> IsOrgMemberAsync(string token, string org, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsTeamMemberAsync(string token, string org, string team, string login, CancellationToken ct) => Task.FromResult(false);

        // By-login probes the friends feature actually uses.
        public Task<bool> UserExistsAsync(string login, CancellationToken ct) => Task.FromResult(Users.Contains(login));
        public Task<bool> IsOrgMemberByLoginAsync(string org, string login, CancellationToken ct) => Task.FromResult(OrgMembers.Contains((org, login)));
        public Task<bool> IsTeamMemberByLoginAsync(string org, string team, string login, CancellationToken ct) => Task.FromResult(TeamMembers.Contains((org, team, login)));
    }

    private static GitHubAuthOptions Options(params string[] orgs)
        => new() { Enabled = true, ClientId = "cid", AllowedOrgs = orgs };

    private static (FriendStore Friends, GrantStore Grants, FriendEligibilityService Eligibility, FriendService Service, FriendAuthorizer Authorizer)
        Build(FakeGitHub gh, GitHubAuthOptions options, string? dir = null)
    {
        var friends = new FriendStore(dir);
        var grants = new GrantStore(dir, TimeProvider.System);
        var eligibility = new FriendEligibilityService(friends, gh, options);
        var service = new FriendService(friends, grants, eligibility, gh, TimeProvider.System);
        var authorizer = new FriendAuthorizer(grants, gh);
        return (friends, grants, eligibility, service, authorizer);
    }

    // ---- friend directory ----

    [Fact]
    public async Task Add_list_and_remove_a_friend()
    {
        var gh = new FakeGitHub();
        gh.Users.Add("octocat");
        var (_, _, _, service, _) = Build(gh, Options());

        var added = await service.AddFriendAsync("octocat", "The Octocat");
        Assert.Equal("octocat", added.GitHubLogin);
        Assert.Equal(FriendSource.Explicit, added.Source);

        Assert.Contains(service.ListFriends(), f => f.GitHubLogin == "octocat" && f.DisplayName == "The Octocat");

        Assert.True(service.RemoveFriend("octocat"));
        Assert.Empty(service.ListFriends());
    }

    [Fact]
    public async Task Adding_a_non_existent_github_user_is_rejected()
    {
        // Stub models a 404: the handle is not in Users, so UserExistsAsync returns false.
        var gh = new FakeGitHub();
        var (_, _, _, service, _) = Build(gh, Options());

        await Assert.ThrowsAsync<FriendActionException>(() => service.AddFriendAsync("ghost", null));
        Assert.Empty(service.ListFriends());
    }

    [Fact]
    public async Task Friends_survive_a_reload_from_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agnes-friends-" + Guid.NewGuid().ToString("n"));
        try
        {
            var gh = new FakeGitHub();
            gh.Users.Add("alice");
            var first = new FriendService(new FriendStore(dir), new GrantStore(dir, TimeProvider.System),
                new FriendEligibilityService(new FriendStore(dir), gh, Options()), gh, TimeProvider.System);
            await first.AddFriendAsync("alice", null);

            var reloaded = new FriendStore(dir);
            Assert.True(reloaded.Contains("alice"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- eligibility (live, never cached as trust) ----

    [Fact]
    public async Task Shared_org_membership_makes_a_user_eligible()
    {
        var gh = new FakeGitHub();
        gh.OrgMembers.Add(("acme", "owner"));
        gh.OrgMembers.Add(("acme", "alice"));
        var (_, _, eligibility, _, _) = Build(gh, Options("acme"));

        Assert.True(await eligibility.IsEligibleAsync("owner", "alice"));
    }

    [Fact]
    public async Task No_shared_org_and_not_a_friend_is_not_eligible()
    {
        var gh = new FakeGitHub();
        gh.OrgMembers.Add(("acme", "owner")); // owner is in acme, but the target is not
        var (_, _, eligibility, _, _) = Build(gh, Options("acme"));

        Assert.False(await eligibility.IsEligibleAsync("owner", "stranger"));
    }

    [Fact]
    public async Task An_explicit_friend_is_eligible_without_any_shared_org()
    {
        var gh = new FakeGitHub();
        gh.Users.Add("bob");
        var (friends, _, eligibility, service, _) = Build(gh, Options("acme"));
        await service.AddFriendAsync("bob", null);
        Assert.True(friends.Contains("bob"));

        Assert.True(await eligibility.IsEligibleAsync("owner", "bob"));
    }

    [Fact]
    public async Task Shared_team_membership_makes_a_user_eligible()
    {
        var gh = new FakeGitHub();
        gh.TeamMembers.Add(("acme", "eng", "owner"));
        gh.TeamMembers.Add(("acme", "eng", "carol"));
        var (_, _, eligibility, _, _) = Build(gh, Options("acme/eng"));

        Assert.True(await eligibility.IsEligibleAsync("owner", "carol"));
    }

    [Fact]
    public async Task Eligibility_is_recomputed_live_so_changing_membership_flips_the_result()
    {
        var gh = new FakeGitHub();
        gh.OrgMembers.Add(("acme", "owner"));
        gh.OrgMembers.Add(("acme", "alice"));
        var (_, _, eligibility, _, _) = Build(gh, Options("acme"));

        Assert.True(await eligibility.IsEligibleAsync("owner", "alice"));

        // Revoke alice's org membership at GitHub — no cached trust bit, so the very next check flips to false.
        gh.OrgMembers.Remove(("acme", "alice"));
        Assert.False(await eligibility.IsEligibleAsync("owner", "alice"));
    }

    // ---- explicit, revocable grants + enforcement ----

    [Fact]
    public async Task Granting_requires_an_eligible_grantee()
    {
        var gh = new FakeGitHub();
        var (_, _, _, service, _) = Build(gh, Options("acme"));

        // Not a friend and shares no org → ineligible → grant refused.
        await Assert.ThrowsAsync<FriendActionException>(
            () => service.GrantAsync("owner", "stranger", "host:main", GrantScope.ReadOnly, "device-1"));
        Assert.Empty(service.ListGrants());
    }

    [Fact]
    public async Task Authorize_allows_a_covered_non_revoked_grant_and_denies_after_revoke()
    {
        var gh = new FakeGitHub();
        gh.Users.Add("bob");
        var (_, _, _, service, authorizer) = Build(gh, Options());
        await service.AddFriendAsync("bob", null);

        var grant = await service.GrantAsync("owner", "bob", "host:main", GrantScope.Collaborate, "device-1");
        Assert.True(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.Collaborate));

        Assert.True(service.RevokeGrant(grant.Id));

        // A revoked grant can never again authorize anything.
        Assert.False(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.Collaborate));
        Assert.False(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.ReadOnly));
    }

    [Fact]
    public async Task Authorize_denies_a_scope_escalation()
    {
        var gh = new FakeGitHub();
        gh.Users.Add("bob");
        var (_, _, _, service, authorizer) = Build(gh, Options());
        await service.AddFriendAsync("bob", null);

        await service.GrantAsync("owner", "bob", "host:main", GrantScope.ReadOnly, "device-1");

        // ReadOnly grant does not satisfy a Collaborate requirement …
        Assert.False(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.Collaborate));
        // … but does satisfy a ReadOnly requirement.
        Assert.True(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.ReadOnly));
    }

    [Fact]
    public async Task Knowing_a_github_handle_with_no_grant_authorizes_nothing()
    {
        var gh = new FakeGitHub();
        gh.Users.Add("mallory"); // a perfectly real GitHub account …
        var (_, _, _, _, authorizer) = Build(gh, Options());

        // … but with no grant covering the resource, handle-knowledge alone authorizes nothing.
        Assert.False(await authorizer.AuthorizeAsync("mallory", "host:main", GrantScope.ReadOnly));
    }

    [Fact]
    public async Task Authorize_denies_when_the_actors_github_identity_is_no_longer_valid()
    {
        var gh = new FakeGitHub();
        gh.Users.Add("bob");
        var (_, _, _, service, authorizer) = Build(gh, Options());
        await service.AddFriendAsync("bob", null);
        await service.GrantAsync("owner", "bob", "host:main", GrantScope.ReadOnly, "device-1");
        Assert.True(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.ReadOnly));

        // Bob's GitHub account disappears (renamed/deleted). The grant still exists, but the live identity
        // re-check fails, so authorization is denied — no authorizing on the grant record alone.
        gh.Users.Remove("bob");
        Assert.False(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.ReadOnly));
    }

    [Fact]
    public async Task Revoking_is_permanent_and_a_grant_is_revoked_only_once()
    {
        var gh = new FakeGitHub();
        gh.Users.Add("bob");
        var (_, grants, _, service, _) = Build(gh, Options());
        await service.AddFriendAsync("bob", null);
        var grant = await service.GrantAsync("owner", "bob", "host:main", GrantScope.ReadOnly, "device-1");

        Assert.True(service.RevokeGrant(grant.Id));
        Assert.False(service.RevokeGrant(grant.Id)); // already revoked → no-op

        Assert.Empty(grants.ListActive());
        Assert.False(grants.Find(grant.Id)!.IsActive); // retained for audit, but inactive
    }

    [Fact]
    public void Device_github_login_is_resolved_from_the_paired_subject()
    {
        var path = Path.Combine(Path.GetTempPath(), "agnes-devreg-" + Guid.NewGuid().ToString("n"), "devices.json");
        try
        {
            var reg = new DeviceRegistry(bootstrapToken: null, dataFilePath: path);
            var github = reg.IssueDeviceToken("laptop", subject: "github:alice", kind: "github");
            Assert.Equal("alice", reg.ResolveGitHubLogin(github.Token));

            // A non-GitHub-paired device (or an unknown token) has no GitHub login.
            var paired = reg.IssueDeviceToken("phone", subject: "pairing", kind: "pairing");
            Assert.Null(reg.ResolveGitHubLogin(paired.Token));
            Assert.Null(reg.ResolveGitHubLogin("unknown-token"));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Removing_a_friend_does_not_revoke_an_existing_grant()
    {
        // AC3 analogue: contact removal is independent of access already granted.
        var gh = new FakeGitHub();
        gh.Users.Add("bob");
        var (_, _, _, service, authorizer) = Build(gh, Options());
        await service.AddFriendAsync("bob", null);
        await service.GrantAsync("owner", "bob", "host:main", GrantScope.ReadOnly, "device-1");

        Assert.True(service.RemoveFriend("bob"));

        // The grant is a separate, still-active fact; only an explicit revoke tears it down.
        Assert.True(await authorizer.AuthorizeAsync("bob", "host:main", GrantScope.ReadOnly));
    }
}
