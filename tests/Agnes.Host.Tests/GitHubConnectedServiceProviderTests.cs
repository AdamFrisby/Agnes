using System.Net;
using System.Security.Cryptography;
using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Tests;

/// <summary>
/// The first REAL connected-service provider (.ideas/providers/02): GitHub, reusing Agnes's existing GitHub
/// credential sources (App installation token / stored PAT) rather than adding a new OAuth flow. Everything
/// here is offline — a stub <see cref="HttpMessageHandler"/> stands in where a token is minted, and fakes
/// stand in for the app/stored sources; NO network.
/// </summary>
public class GitHubConnectedServiceProviderTests
{
    private static ConnectedServiceProfile Profile(string account = "personal")
        => new(Id: "gh-profile", ProviderId: GitHubConnectedServiceProvider.ProviderId, DisplayName: "GitHub", AccountLabel: account);

    [Fact]
    public async Task App_source_mints_a_short_lived_token_with_its_real_expiry_and_a_github_token_env()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var fakeApp = new FakeCredentialSource(new GitCredential("x-access-token", "ghs_installation", expiry));
        var provider = new GitHubConnectedServiceProvider(appSource: () => fakeApp, storedSource: () => null);

        var resolved = await provider.ResolveAsync(Profile());

        Assert.Equal("ghs_installation", resolved.Value);
        Assert.Equal(expiry, resolved.ExpiresAt);
        Assert.NotNull(resolved.Env);
        Assert.Equal("ghs_installation", resolved.Env![GitHubConnectedServiceProvider.TokenEnvVar]);
    }

    [Fact]
    public async Task Stored_token_is_returned_as_the_credential_when_no_app_is_linked()
    {
        var stored = new StoredTokenCredentialSource(GitHubConnectedServiceProvider.GitHubHost, "ghp_stored_pat");
        var provider = new GitHubConnectedServiceProvider(appSource: () => null, storedSource: () => stored);

        var resolved = await provider.ResolveAsync(Profile());

        Assert.Equal("ghp_stored_pat", resolved.Value);
        Assert.Null(resolved.ExpiresAt); // a static PAT carries no expiry
        Assert.Equal("ghp_stored_pat", resolved.Env![GitHubConnectedServiceProvider.TokenEnvVar]);
    }

    [Fact]
    public async Task App_token_is_preferred_over_a_stored_token_when_both_are_configured()
    {
        var fakeApp = new FakeCredentialSource(new GitCredential("x-access-token", "ghs_app", DateTimeOffset.UtcNow.AddHours(1)));
        var stored = new StoredTokenCredentialSource(GitHubConnectedServiceProvider.GitHubHost, "ghp_stored");
        var provider = new GitHubConnectedServiceProvider(appSource: () => fakeApp, storedSource: () => stored);

        var resolved = await provider.ResolveAsync(Profile());

        Assert.Equal("ghs_app", resolved.Value); // short-lived App token wins
    }

    [Fact]
    public async Task Throws_a_clear_error_when_no_github_auth_is_configured()
    {
        // Fail-loud: a silent unauthenticated resolve would let a CLI launch masquerade as authenticated.
        var provider = new GitHubConnectedServiceProvider(appSource: () => null, storedSource: () => null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResolveAsync(Profile()));
        Assert.Contains("GitHub", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_app_source_that_cannot_mint_falls_through_to_the_stored_token()
    {
        // e.g. the linked App has no installation covering this account — the source returns null, not a throw.
        var emptyApp = new FakeCredentialSource(null);
        var stored = new StoredTokenCredentialSource(GitHubConnectedServiceProvider.GitHubHost, "ghp_fallback");
        var provider = new GitHubConnectedServiceProvider(appSource: () => emptyApp, storedSource: () => stored);

        Assert.Equal("ghp_fallback", (await provider.ResolveAsync(Profile())).Value);
    }

    [Fact]
    public async Task The_broker_routes_a_github_profile_to_this_provider_by_id_with_no_broker_change()
    {
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, GitHubConnectedServiceProvider.ProviderId, "GitHub", "personal"));

        var fakeApp = new FakeCredentialSource(new GitCredential("x-access-token", "ghs_via_broker", DateTimeOffset.UtcNow.AddHours(1)));
        var provider = new GitHubConnectedServiceProvider(appSource: () => fakeApp, storedSource: () => null);
        var registry = new PluginRegistry<IConnectedServiceProvider>(
            new IConnectedServiceProvider[] { new TemplateConnectedServiceProvider(), provider }, p => p.Id);
        var broker = new ConnectedServiceBroker(store, registry);

        var resolved = await broker.ResolveAsync(profile.Id);

        Assert.Equal("ghs_via_broker", resolved.Value);
    }

    [Fact]
    public async Task Reuses_the_real_app_source_end_to_end_without_leaking_the_private_key_or_signing_material()
    {
        // Drive the REAL GitHubAppCredentialSource through a stub handler so a token is minted exactly as in
        // production, then assert the App private key (and the minting JWT) never leak into the result.
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        string? sentAuthHeader = null;
        var handler = new StubHandler((req, _) =>
        {
            sentAuthHeader = req.Headers.Authorization?.ToString(); // the "Bearer <jwt>" used to mint
            return Json($$"""{"token":"ghs_real_minted","expires_at":"{{expiry:o}}"}""");
        });
        var appSource = new GitHubAppCredentialSource(
            () => new[] { new GitHubAppConfig("12345", "agnes-host", 99, pem, "octo-org") }, new HttpClient(handler));
        var provider = new GitHubConnectedServiceProvider(appSource: () => appSource, storedSource: () => null);

        // AccountLabel carries the owner/repo so the real source routes + scopes the token.
        var resolved = await provider.ResolveAsync(
            new ConnectedServiceProfile("p", GitHubConnectedServiceProvider.ProviderId, "GitHub", "octo-org/service"));

        Assert.Equal("ghs_real_minted", resolved.Value);
        Assert.Equal(expiry, resolved.ExpiresAt);

        // Secret hygiene: the resolved value/env is ONLY the short-lived token — no PEM bytes, no signing JWT.
        var jwt = sentAuthHeader!.Split(' ')[1];
        var body = resolved.Value + "|" + string.Join("|", resolved.Env!.Select(kv => kv.Key + "=" + kv.Value));
        foreach (var secret in new[] { pem, jwt, "PRIVATE KEY", "-----BEGIN" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }
    }

    /// <summary>A stand-in for one of the injected GitHub credential sources — returns a fixed credential.</summary>
    private sealed class FakeCredentialSource(GitCredential? credential) : ICredentialSource
    {
        public bool Handles(string host) => string.Equals(host, GitHubConnectedServiceProvider.GitHubHost, StringComparison.OrdinalIgnoreCase);

        public Task<GitCredential?> ResolveAsync(CredentialRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(credential);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }
}
