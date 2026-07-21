using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class GitHubIdentityTests
{
    private sealed class FakeLookup : IGitHubUserLookup
    {
        public string? Login { get; set; }
        public HashSet<string> Orgs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Org, string Team)> Teams { get; } = new();

        public Task<string?> GetLoginAsync(string token, CancellationToken ct) => Task.FromResult(Login);
        public Task<bool> IsOrgMemberAsync(string token, string org, CancellationToken ct) => Task.FromResult(Orgs.Contains(org));
        public Task<bool> IsTeamMemberAsync(string token, string org, string team, string login, CancellationToken ct)
            => Task.FromResult(Teams.Contains((org, team)));
    }

    private static GitHubIdentity Make(FakeLookup lookup, string[]? users = null, string[]? orgs = null)
        => new(lookup, new GitHubAuthOptions
        {
            Enabled = true,
            ClientId = "client-id",
            AllowedUsers = users ?? [],
            AllowedOrgs = orgs ?? [],
        });

    [Fact]
    public async Task Allowlisted_user_is_accepted_case_insensitively()
    {
        var id = Make(new FakeLookup { Login = "Alice" }, users: ["alice"]);
        Assert.Equal("Alice", await id.VerifyAsync("tok"));
    }

    [Fact]
    public async Task User_not_on_the_allowlist_is_rejected()
    {
        var id = Make(new FakeLookup { Login = "mallory" }, users: ["alice"]);
        Assert.Null(await id.VerifyAsync("tok"));
    }

    [Fact]
    public async Task Org_member_is_accepted()
    {
        var lookup = new FakeLookup { Login = "bob" };
        lookup.Orgs.Add("acme");
        var id = Make(lookup, orgs: ["acme"]);
        Assert.Equal("bob", await id.VerifyAsync("tok"));
    }

    [Fact]
    public async Task Team_member_is_accepted_and_non_member_of_the_org_is_not()
    {
        var lookup = new FakeLookup { Login = "carol" };
        lookup.Teams.Add(("acme", "eng"));
        Assert.Equal("carol", await Make(lookup, orgs: ["acme/eng"]).VerifyAsync("tok"));

        // Same user, but only allowed via a team they're not on → rejected.
        var other = new FakeLookup { Login = "carol" };
        Assert.Null(await Make(other, orgs: ["acme/ops"]).VerifyAsync("tok"));
    }

    [Fact]
    public async Task Not_usable_with_an_empty_allowlist()
    {
        // Enabled + client id but no users/orgs → not usable → always null.
        var id = Make(new FakeLookup { Login = "alice" });
        Assert.Null(await id.VerifyAsync("tok"));
    }

    [Fact]
    public async Task Null_token_or_unknown_login_is_rejected()
    {
        var id = Make(new FakeLookup { Login = null }, users: ["alice"]);
        Assert.Null(await id.VerifyAsync("tok"));
        Assert.Null(await id.VerifyAsync(null));
    }
}
