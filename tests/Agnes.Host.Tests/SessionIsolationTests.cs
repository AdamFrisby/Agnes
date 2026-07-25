using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Groups;
using Agnes.Host.Sessions;
using Agnes.Host.Sharing;

namespace Agnes.Host.Tests;

/// <summary>
/// Covers session-isolation (Agnes:Security:SessionIsolation): the group plugin point + GitHub-repo provider,
/// the membership service, the authorizer's additive owner/group grants, and owner/group persistence.
/// </summary>
public class SessionIsolationTests
{
    private static SharingCaller User(string login) => new(DeviceId: "dev-" + login, GitHubLogin: login, IsOwner: false);
    private static SharingCaller HostOwner => new(DeviceId: "owner-dev", GitHubLogin: null, IsOwner: true);

    private sealed class FakeWriteAccess : IGitHubRepoWriteAccess
    {
        public HashSet<(string Owner, string Repo, string Login)> Writers { get; } = new();
        public Task<bool> HasWriteAccessAsync(string owner, string repo, string login, CancellationToken cancellationToken = default)
            => Task.FromResult(Writers.Contains((owner, repo, login)));
    }

    private static GroupMembershipService Groups(FakeWriteAccess access)
        => new(new PluginRegistry<IGroupProvider>([new GitHubRepoGroupProvider(access)], p => p.Id));

    private static SessionAccessAuthorizer Authorizer(SessionIsolation isolation, GroupMembershipService? groups = null)
        => new(new SessionShareStore(directory: null), groups, new SessionSecurityOptions { SessionIsolation = isolation });

    // ---- group id parsing (A1) ----

    [Theory]
    [InlineData("github.com/octo/repo", true, "octo", "repo")]
    [InlineData("octo/repo", true, "octo", "repo")]
    [InlineData("gitlab.com/o/r", false, "", "")]   // three parts but not github.com
    [InlineData("justone", false, "", "")]
    [InlineData("", false, "", "")]
    public void Repo_group_id_parses_owner_and_repo(string id, bool ok, string owner, string repo)
    {
        Assert.Equal(ok, GitHubRepoGroupProvider.TryParseRepo(id, out var o, out var r));
        if (ok)
        {
            Assert.Equal(owner, o);
            Assert.Equal(repo, r);
        }
    }

    // ---- GitHub group provider (A1) ----

    [Fact]
    public async Task Github_group_membership_reflects_repo_write_access()
    {
        var access = new FakeWriteAccess { Writers = { ("octo", "repo", "alice") } };
        var provider = new GitHubRepoGroupProvider(access);

        Assert.True(provider.Handles("github.com/octo/repo"));
        Assert.True(await provider.IsMemberAsync(new GroupPrincipal("d", "alice"), "github.com/octo/repo"));
        Assert.False(await provider.IsMemberAsync(new GroupPrincipal("d", "bob"), "github.com/octo/repo")); // no write access
        Assert.False(await provider.IsMemberAsync(new GroupPrincipal("d", null), "github.com/octo/repo"));  // no GitHub identity
    }

    [Fact]
    public async Task Membership_service_is_empty_without_providers()
    {
        var svc = new GroupMembershipService();
        Assert.False(svc.HasProviders);
        Assert.False(await svc.IsMemberAsync(new GroupPrincipal("d", "alice"), "github.com/octo/repo"));
    }

    // ---- authorizer isolation grants (A3) ----

    [Fact]
    public async Task Shared_mode_adds_no_owner_grant()
    {
        var auth = Authorizer(SessionIsolation.Shared);
        Assert.True(auth.IsolationDisabled);
        // alice "owns" the session, but Shared mode grants nothing beyond shares / host-owner.
        Assert.False(await auth.CanSubscribeAsync("s", owner: "alice", group: null, User("alice")));
    }

    [Fact]
    public async Task Per_user_lets_the_owner_reach_their_own_session()
    {
        var auth = Authorizer(SessionIsolation.PerUser);
        Assert.True(await auth.CanSubscribeAsync("s", "alice", null, User("alice")));
        Assert.True(await auth.CanPromptAsync("s", "alice", null, User("alice")));
        Assert.True(await auth.CanManageAsync("s", "alice", null, User("alice"))); // the owner may manage sharing
        Assert.False(await auth.CanSubscribeAsync("s", "alice", null, User("bob"))); // a different user cannot
    }

    [Fact]
    public async Task Per_group_lets_members_read_and_drive_but_not_manage()
    {
        var access = new FakeWriteAccess { Writers = { ("octo", "repo", "bob") } };
        var auth = Authorizer(SessionIsolation.PerGroup, Groups(access));
        const string group = "github.com/octo/repo";

        // bob doesn't own the session but has write access to its repo group.
        Assert.True(await auth.CanSubscribeAsync("s", owner: "alice", group, User("bob")));
        Assert.True(await auth.CanPromptAsync("s", "alice", group, User("bob")));
        Assert.False(await auth.CanManageAsync("s", "alice", group, User("bob"))); // group membership ≠ manage
        Assert.False(await auth.CanSubscribeAsync("s", "alice", group, User("carol"))); // non-member, non-owner denied
    }

    [Fact]
    public async Task Host_owner_still_reaches_everything_under_isolation()
    {
        var auth = Authorizer(SessionIsolation.PerUser);
        Assert.True(await auth.CanSubscribeAsync("s", owner: "alice", group: null, HostOwner));
        Assert.True(await auth.CanManageAsync("s", owner: "alice", group: null, HostOwner));
    }

    // ---- owner/group persistence (A2) ----

    [Fact]
    public async Task Sqlite_round_trips_session_owner_and_group()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-own-{Guid.NewGuid():n}.db");
        try
        {
            using var store = new SqliteEventStore(path);
            await store.SaveSessionAsync(new SessionRecord(
                "s1", "codex", "/w", null, false, false, true, DateTimeOffset.UtcNow,
                Owner: "alice", Group: "github.com/octo/repo"));

            var record = Assert.Single(await store.ListSessionsAsync());
            Assert.Equal("alice", record.Owner);
            Assert.Equal("github.com/octo/repo", record.Group);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
