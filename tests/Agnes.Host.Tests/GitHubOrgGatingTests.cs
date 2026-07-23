using System.Net;
using System.Text;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// GitHub org/team allowlist gating (AC3): a GitHub login that authenticates but is not a member of an
/// allowed org/team is rejected with the distinguishable <see cref="GitHubAuthOutcome.NotAllowlisted"/>
/// outcome, separate from a plain bad token. Includes a coverage path over the real
/// <see cref="GitHubUserLookup"/> driven by a stubbed <see cref="HttpMessageHandler"/> (no network).
/// </summary>
public class GitHubOrgGatingTests
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

    private static GitHubIdentity Make(FakeLookup lookup, string[]? orgs = null)
        => new(lookup, new GitHubAuthOptions { Enabled = true, ClientId = "cid", AllowedOrgs = orgs ?? [] });

    [Fact]
    public async Task Org_member_is_allowed()
    {
        var lookup = new FakeLookup { Login = "bob" };
        lookup.Orgs.Add("acme");
        var result = await Make(lookup, orgs: ["acme"]).VerifyDetailedAsync("tok");
        Assert.Equal(GitHubAuthOutcome.Allowed, result.Outcome);
        Assert.Equal("bob", result.Login);
    }

    [Fact]
    public async Task Authenticated_non_member_is_rejected_distinguishably_and_keeps_the_login()
    {
        var result = await Make(new FakeLookup { Login = "mallory" }, orgs: ["acme"]).VerifyDetailedAsync("tok");
        Assert.Equal(GitHubAuthOutcome.NotAllowlisted, result.Outcome);
        Assert.Equal("mallory", result.Login);   // login preserved so the error can name the account
    }

    [Fact]
    public async Task Bad_token_is_a_different_outcome_than_a_gated_out_account()
    {
        // No login resolvable → invalid token, not "not allowlisted".
        var result = await Make(new FakeLookup { Login = null }, orgs: ["acme"]).VerifyDetailedAsync("tok");
        Assert.Equal(GitHubAuthOutcome.InvalidToken, result.Outcome);
        Assert.Null(result.Login);
    }

    [Fact]
    public async Task Team_membership_gates_correctly()
    {
        var member = new FakeLookup { Login = "carol" };
        member.Teams.Add(("acme", "eng"));
        Assert.Equal(GitHubAuthOutcome.Allowed, (await Make(member, orgs: ["acme/eng"]).VerifyDetailedAsync("tok")).Outcome);

        var nonMember = new FakeLookup { Login = "carol" };
        Assert.Equal(GitHubAuthOutcome.NotAllowlisted, (await Make(nonMember, orgs: ["acme/ops"]).VerifyDetailedAsync("tok")).Outcome);
    }

    // ---- coverage over the real lookup with a stubbed HTTP transport (AC3: "mocked GitHub API response") ----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GitHubIdentity WithStub(Func<HttpRequestMessage, HttpResponseMessage> respond, string[] orgs)
    {
        var http = new HttpClient(new StubHandler(respond));
        return new GitHubIdentity(new GitHubUserLookup(http), new GitHubAuthOptions { Enabled = true, ClientId = "cid", AllowedOrgs = orgs });
    }

    [Fact]
    public async Task Real_lookup_accepts_an_active_org_member_via_mocked_api()
    {
        var id = WithStub(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/user")
            {
                return Json(HttpStatusCode.OK, "{\"login\":\"octocat\"}");
            }

            if (path == "/user/memberships/orgs/acme")
            {
                return Json(HttpStatusCode.OK, "{\"state\":\"active\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, orgs: ["acme"]);

        var result = await id.VerifyDetailedAsync("gh-token");
        Assert.Equal(GitHubAuthOutcome.Allowed, result.Outcome);
        Assert.Equal("octocat", result.Login);
    }

    [Fact]
    public async Task Real_lookup_rejects_a_non_member_distinguishably_via_mocked_api()
    {
        var id = WithStub(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/user")
            {
                return Json(HttpStatusCode.OK, "{\"login\":\"outsider\"}");
            }

            // GitHub returns 404 for a non-member membership check.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, orgs: ["acme"]);

        var result = await id.VerifyDetailedAsync("gh-token");
        Assert.Equal(GitHubAuthOutcome.NotAllowlisted, result.Outcome);
        Assert.Equal("outsider", result.Login);
    }
}
