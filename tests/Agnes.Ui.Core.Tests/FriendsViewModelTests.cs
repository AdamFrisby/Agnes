using Agnes.Abstractions;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The friends/grants surface, from the user's side: a grant names something you can point at rather than an
/// opaque id you have to guess, attempting one with nothing to point at explains itself, and a listed friend's
/// eligibility can be checked from their own row.
/// </summary>
public class FriendsViewModelTests
{
    private static readonly GrantTarget Host = new("host:https://box:7777", "All of box");
    private static readonly GrantTarget Session = new("session:abc", "Session — refactor");

    [Fact]
    public async Task Granting_uses_the_picked_target_rather_than_typed_text()
    {
        var host = new FakeFriendHost { Eligible = true };
        host.Seed(new Friend("bob", "Bob", DateTimeOffset.UnixEpoch, FriendSource.Explicit));
        var vm = new FriendsViewModel(() => host, ImmediateDispatcher.Instance, () => [Host, Session]);
        await vm.RefreshAsync();

        vm.SelectedFriend = vm.Friends.Single(f => f.GitHubLogin == "bob");
        vm.SelectedGrantTarget = vm.GrantTargets.Single(t => t.Id == Session.Id);
        vm.NewGrantScope = GrantScope.Collaborate;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.GrantCommand).ExecuteAsync(null);

        var grant = Assert.Single(host.Grants);
        Assert.Equal("bob", grant.GranteeLogin);
        Assert.Equal("session:abc", grant.Resource);
        Assert.Equal(GrantScope.Collaborate, grant.Scope);
    }

    [Fact]
    public async Task The_first_target_is_preselected_so_granting_needs_one_choice_not_two()
    {
        var host = new FakeFriendHost();
        host.Seed(new Friend("bob", null, DateTimeOffset.UnixEpoch, FriendSource.Explicit));
        var vm = new FriendsViewModel(() => host, ImmediateDispatcher.Instance, () => [Host, Session]);

        await vm.RefreshAsync();

        Assert.True(vm.HasGrantTargets);
        Assert.Equal(Host.Id, vm.SelectedGrantTarget?.Id);
    }

    [Fact]
    public async Task With_nothing_to_grant_against_the_attempt_says_why()
    {
        var host = new FakeFriendHost();
        host.Seed(new Friend("bob", null, DateTimeOffset.UnixEpoch, FriendSource.Explicit));
        var vm = new FriendsViewModel(() => host, ImmediateDispatcher.Instance, () => []);
        await vm.RefreshAsync();
        vm.SelectedFriend = vm.Friends[0];

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.GrantCommand).ExecuteAsync(null);

        Assert.Empty(host.Grants);
        Assert.Contains("connect a host", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_listed_friend_can_be_checked_without_retyping_their_handle()
    {
        var host = new FakeFriendHost { Eligible = false };
        host.Seed(new Friend("bob", null, DateTimeOffset.UnixEpoch, FriendSource.SharedOrg));
        var vm = new FriendsViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();

        var row = Assert.Single(vm.Friends);
        Assert.Equal("shares a configured org/team", row.SourceLabel);
        Assert.False(row.HasEligibilityNote);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<FriendRowVm>)vm.CheckFriendEligibilityCommand).ExecuteAsync(row);

        Assert.True(row.HasEligibilityNote);
        Assert.Contains("not eligible", row.EligibilityNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bob", host.LastEligibilityCheck);
    }

    /// <summary>An in-memory friend directory and grant store.</summary>
    private sealed class FakeFriendHost : StubAgnesHost
    {
        private readonly List<Friend> _friends = [];

        public List<AccessGrant> Grants { get; } = [];

        public bool Eligible { get; init; }

        public string? LastEligibilityCheck { get; private set; }

        public void Seed(Friend friend) => _friends.Add(friend);

        public override Task<IReadOnlyList<Friend>> ListFriendsAsync()
            => Task.FromResult<IReadOnlyList<Friend>>(_friends.ToArray());

        public override Task<IReadOnlyList<AccessGrant>> ListGrantsAsync()
            => Task.FromResult<IReadOnlyList<AccessGrant>>(Grants.ToArray());

        public override Task<bool> CheckEligibilityAsync(string gitHubLogin)
        {
            LastEligibilityCheck = gitHubLogin;
            return Task.FromResult(Eligible);
        }

        public override Task<AccessGrant> GrantAccessAsync(string granteeLogin, string resource, GrantScope scope)
        {
            var grant = new AccessGrant(
                Guid.NewGuid().ToString("n"), granteeLogin, resource, scope, DateTimeOffset.UnixEpoch, "device-1");
            Grants.Add(grant);
            return Task.FromResult(grant);
        }
    }
}
