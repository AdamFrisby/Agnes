using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// The auth endpoints end-to-end over the real host: <c>/auth/methods</c> reflects config, GitHub-SSO
/// exchange issues a working device token for an allowlisted user (and rejects others), and the
/// pairing bootstrap is refused when disabled. GitHub is faked so no network is touched.
/// </summary>
public class GitHubAuthTests
{
    private sealed class FakeLookup : IGitHubUserLookup
    {
        public Task<string?> GetLoginAsync(string token, CancellationToken ct)
            => Task.FromResult<string?>(token == "good-gh-token" ? "alice" : null);

        public Task<bool> IsOrgMemberAsync(string token, string org, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsTeamMemberAsync(string token, string org, string team, string login, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:Auth:Pairing:Enabled"] = "false",       // GitHub-only host
                    ["Agnes:Auth:GitHub:Enabled"] = "true",
                    ["Agnes:Auth:GitHub:ClientId"] = "test-client-id",
                    ["Agnes:Auth:GitHub:AllowedUsers:0"] = "alice",
                }));
            builder.ConfigureServices(s => s.AddSingleton<IGitHubUserLookup>(new FakeLookup()));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Methods_reflect_config_and_github_exchange_issues_a_usable_token()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        var methods = await http.GetFromJsonAsync<AuthMethods>("/auth/methods");
        Assert.NotNull(methods);
        Assert.False(methods!.Pairing);                 // pairing disabled
        Assert.True(methods.GitHub);
        Assert.Equal("test-client-id", methods.GitHubClientId);

        // Not-allowlisted GitHub token → 403.
        var bad = await http.PostAsJsonAsync("/auth/github/exchange", new GitHubExchangeRequest("nope", "laptop"));
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);

        // Allowlisted GitHub token → an Agnes device token.
        var ok = await http.PostAsJsonAsync("/auth/github/exchange", new GitHubExchangeRequest("good-gh-token", "laptop"));
        ok.EnsureSuccessStatusCode();
        var paired = await ok.Content.ReadFromJsonAsync<PairResponse>();
        Assert.NotNull(paired);
        Assert.Contains("laptop", paired!.DeviceName);

        // The issued token authorizes a protected endpoint.
        var req = new HttpRequestMessage(HttpMethod.Get, "/devices");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", paired.Token);
        using var devices = await http.SendAsync(req);
        devices.EnsureSuccessStatusCode();

        // Pairing is refused while disabled.
        var pair = await http.PostAsJsonAsync("/pair", new PairRequest("WHATEVER", "x"));
        Assert.Equal(HttpStatusCode.BadRequest, pair.StatusCode);
    }
}
