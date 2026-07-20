using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Agnes.Host.Hosting;
using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Tests;

public class GitHubAppTests
{
    private static string TestPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    [Fact]
    public async Task Mints_a_repo_scoped_installation_token()
    {
        string? body = null;
        string? auth = null;
        var handler = new FakeHandler((req, b) =>
        {
            body = b;
            auth = req.Headers.Authorization?.ToString();
            return Json($$"""{"token":"ghs_scoped","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):o}}"}""");
        });
        var source = new GitHubAppCredentialSource(() => new[] { new GitHubAppConfig("12345", "agnes-host", 99, TestPem()) }, new HttpClient(handler));

        var cred = await source.ResolveAsync(new CredentialRequest("https", "github.com", "AdamFrisby/Agnes", "get"));

        Assert.NotNull(cred);
        Assert.Equal("x-access-token", cred!.Username);
        Assert.Equal("ghs_scoped", cred.Password);
        Assert.Contains("\"repositories\":[\"Agnes\"]", body);      // scoped to the one repo (short name)
        Assert.Contains("\"contents\":\"write\"", body);            // write-only
        Assert.StartsWith("Bearer ", auth);                         // authed as the app via a JWT
        Assert.Equal(3, auth!.Split(' ')[1].Split('.').Length);     // header.payload.signature
    }

    [Fact]
    public async Task Caches_the_token_until_near_expiry()
    {
        var handler = new FakeHandler((_, _) => Json($$"""{"token":"ghs_a","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):o}}"}"""));
        var source = new GitHubAppCredentialSource(() => new[] { new GitHubAppConfig("1", "s", 5, TestPem()) }, new HttpClient(handler));

        await source.ResolveAsync(new CredentialRequest("https", "github.com", "a/b", "get"));
        await source.ResolveAsync(new CredentialRequest("https", "github.com", "a/b", "get"));

        Assert.Equal(1, handler.Calls); // second call served from cache, no re-mint
    }

    [Fact]
    public async Task Returns_null_without_a_repo_or_installation()
    {
        var handler = new FakeHandler((_, _) => Json("""{"token":"x"}"""));
        var noRepo = new GitHubAppCredentialSource(() => new[] { new GitHubAppConfig("1", "s", 5, TestPem()) }, new HttpClient(handler));
        Assert.Null(await noRepo.ResolveAsync(new CredentialRequest("https", "github.com", null, "get")));

        var noInstall = new GitHubAppCredentialSource(() => new[] { new GitHubAppConfig("1", "s", 0, TestPem()) }, new HttpClient(handler));
        Assert.Null(await noInstall.ResolveAsync(new CredentialRequest("https", "github.com", "a/b", "get")));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Manifest_requests_contents_write_and_points_back_to_the_host()
    {
        var manifest = GitHubConnectFlow.BuildManifest("http://127.0.0.1:5099");
        Assert.Contains("\"contents\":\"write\"", manifest);
        Assert.Contains("\"metadata\":\"read\"", manifest);
        Assert.Contains("http://127.0.0.1:5099/credentials/github/callback", manifest);
        Assert.Contains("\"public\":false", manifest);
    }

    [Fact]
    public void Conversion_parses_id_slug_and_pem()
    {
        var config = GitHubConnectFlow.ParseConversion("""{"id":42,"slug":"agnes-host","pem":"-----BEGIN KEY-----\nabc\n-----END KEY-----","client_id":"x"}""");
        Assert.NotNull(config);
        Assert.Equal("42", config!.AppId);
        Assert.Equal("agnes-host", config.Slug);
        Assert.Contains("BEGIN KEY", config.PrivateKeyPem);
        Assert.Equal(0, config.InstallationId); // not installed yet
    }

    [Fact]
    public void Store_round_trips_the_app_config()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gh-app-{Guid.NewGuid():n}.json");
        try
        {
            var store = new GitHubAppStore(path);
            Assert.Empty(store.List());
            store.Save(new GitHubAppConfig("7", "slug", 88, "PEMDATA", "me"));
            var loaded = store.List()[0];
            Assert.Equal("7", loaded.AppId);
            Assert.Equal(88, loaded.InstallationId);
            Assert.Equal("PEMDATA", loaded.PrivateKeyPem);
            Assert.Equal("me", loaded.Account);
            Assert.Equal("7", new GitHubAppStore(path).Get("me")!.AppId); // reloads across instances
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Begin_connect_then_start_page_yields_the_manifest_form()
    {
        var flow = NewFlow(out _, out _);
        var url = flow.BeginConnect("http://127.0.0.1:5099");
        var state = url.Split("state=")[1];

        var page = flow.StartPage(state);
        Assert.NotNull(page);
        Assert.Contains("github.com/settings/apps/new", page);
        Assert.Contains("name=\"manifest\"", page);
        Assert.Null(flow.StartPage("bogus-state")); // unknown state is rejected
    }

    [Fact]
    public async Task Install_callback_registers_the_live_minting_source()
    {
        var flow = NewFlow(out var store, out var sources);
        Assert.NotNull(sources.For("github.com")); // one source spans all accounts, registered up front
        store.Save(new GitHubAppConfig("7", "agnes-host", 0, TestPem(), "me")); // app created, not yet installed

        var html = await flow.HandleCallbackAsync(code: null, state: null, installationId: "55");

        Assert.Contains("connected", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(55, store.List().Single().InstallationId); // installation recorded on the pending app
    }

    [Fact]
    public async Task Routes_the_credential_by_repo_owner_across_accounts()
    {
        // Two linked accounts; each mint request should authenticate as the matching owner's App.
        var installations = new List<string>();
        var handler = new FakeHandler((req, _) =>
        {
            installations.Add(req.RequestUri!.AbsolutePath); // /app/installations/{id}/access_tokens
            return Json($$"""{"token":"t","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):o}}"}""");
        });
        var source = new GitHubAppCredentialSource(() => new[]
        {
            new GitHubAppConfig("1", "personal", 111, TestPem(), "me"),
            new GitHubAppConfig("2", "work", 222, TestPem(), "work-org"),
        }, new HttpClient(handler));

        await source.ResolveAsync(new CredentialRequest("https", "github.com", "work-org/service", "get"));
        await source.ResolveAsync(new CredentialRequest("https", "github.com", "me/app", "get"));

        Assert.Contains("/app/installations/222/access_tokens", installations); // work repo → work-org App
        Assert.Contains("/app/installations/111/access_tokens", installations); // personal repo → my App
    }

    private static GitHubConnectFlow NewFlow(out GitHubAppStore store, out CredentialSourceRegistry sources)
    {
        store = new GitHubAppStore(Path.Combine(Path.GetTempPath(), $"gh-{Guid.NewGuid():n}.json"));
        sources = new CredentialSourceRegistry();
        return new GitHubConnectFlow(store, sources, new HttpClient(new FakeHandler((_, _) => Json("{}"))),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubConnectFlow>.Instance);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }
}
