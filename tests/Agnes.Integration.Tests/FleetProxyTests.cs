using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Host.Fleet;
using Agnes.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// The fleet proxy is a route table, not a hole: a paired device reaches exactly the orchestrator routes
/// the apps have a control for, reads with any device and writes with an Owner, the host's key goes on
/// and the device's token does not, bodies are re-shaped rather than forwarded, and everything else under
/// /fleet is a 404 whatever the orchestrator would have said.
/// </summary>
public class FleetProxyTests
{
    private const string BootstrapToken = "fleet-bootstrap";
    private const string OrchestratorKey = "orchestrator-secret";

    /// <summary>Stands in for the orchestrator: records what the host sent and answers with a canned body.</summary>
    private sealed class Orchestrator : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Authorization, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"echo":"{{request.RequestUri.PathAndQuery}}"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly Agnes.TestKit.IsolatedHostHome _home = new();

        public Orchestrator Upstream { get; } = new();
        public bool WithFleet { get; init; } = true;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:Home"] = _home.Path,
                    ["Agnes:PairingToken"] = BootstrapToken,
                    ["Agnes:PublicUrl"] = "http://localhost",
                    ["Agnes:Auth:RateLimit:PerIpPerMinute"] = "1000",
                    ["Agnes:Auth:RateLimit:GlobalPerMinute"] = "10000",
                    ["Agnes:Fleet:Enabled"] = WithFleet ? "true" : "false",
                    ["Agnes:Fleet:BaseUrl"] = "http://orchestrator.test:5836",
                    ["Agnes:Fleet:ApiKey"] = OrchestratorKey,
                }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAgentAdapter>(new EndToEndTests.ScriptedAdapter());
                services.AddSingleton(new FleetProxy(
                    WithFleet ? new FleetOptions("http://orchestrator.test:5836", OrchestratorKey) : null, Upstream));
            });
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _home.Dispose();
        }
    }

    private static async Task<(string Owner, string Member)> DevicesAsync(HostFactory factory, HttpClient http)
    {
        var code = factory.Services.GetRequiredService<Agnes.Host.Hosting.DeviceRegistry>().PairingCode;
        var owner = (await (await http.PostAsJsonAsync("/pair", new PairRequest(code, "owner-laptop"))).Content.ReadFromJsonAsync<PairResponse>())!;
        var grant = (await PairingManagement.MintGrantAsync("http://localhost", owner.Token, httpClient: http))!;
        var member = (await (await http.PostAsJsonAsync("/pair", new PairRequest(grant.Secret, "the-phone"))).Content.ReadFromJsonAsync<PairResponse>())!;
        using var demote = new HttpRequestMessage(HttpMethod.Put, $"/devices/{member.DeviceId}/role") { Content = JsonContent.Create(new DeviceRoleRequest(DeviceRole.Member)) };
        demote.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        (await http.SendAsync(demote)).EnsureSuccessStatusCode();
        return (owner.Token, member.Token);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string path, string? token, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return http.SendAsync(request);
    }

    [Fact]
    public async Task A_read_is_forwarded_with_the_hosts_key_and_without_the_devices_token()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (_, member) = await DevicesAsync(factory, http);

        using var response = await SendAsync(http, HttpMethod.Get, "/fleet/workitems", member);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"echo\":\"/workitems\"", await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(factory.Upstream.Seen);
        Assert.Equal("/workitems", seen.Path);
        Assert.Equal("Bearer " + OrchestratorKey, seen.Authorization);
        Assert.DoesNotContain(member, seen.Authorization);
    }

    [Fact]
    public async Task An_unknown_route_is_a_404_before_the_orchestrator_hears_of_it()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, _) = await DevicesAsync(factory, http);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/fleet/suggestions"),
                     (HttpMethod.Get, "/fleet/workitems/abc/attachments"),
                     (HttpMethod.Post, "/fleet/workitems"),
                     (HttpMethod.Put, "/fleet/workitems/abc/prompt"),
                     (HttpMethod.Post, "/fleet/queue/pause"),
                     (HttpMethod.Get, "/fleet/workitems/../devices"),
                     (HttpMethod.Get, "/fleet/workitems/a%2Fb/questions"),
                 })
        {
            using var response = await SendAsync(http, method, path, owner, method == HttpMethod.Get ? null : "{}");
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path} → {response.StatusCode}");
        }

        Assert.Empty(factory.Upstream.Seen);
    }

    [Fact]
    public async Task Writes_take_an_owner_and_bodies_are_reshaped_not_forwarded()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, member) = await DevicesAsync(factory, http);

        using (var refused = await SendAsync(http, HttpMethod.Post, "/fleet/workitems/wi-1/retry", member))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        Assert.Empty(factory.Upstream.Seen);

        using (var retried = await SendAsync(http, HttpMethod.Post, "/fleet/workitems/wi-1/retry", owner))
        {
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        }
        Assert.Equal((HttpMethod.Post, "/workitems/wi-1/retry"), (factory.Upstream.Seen[^1].Method, factory.Upstream.Seen[^1].Path));

        // The ceiling patch carries one field; anything else in the body is dropped, and a body without
        // the field is refused rather than forwarded as an empty patch.
        using (var patched = await SendAsync(http, HttpMethod.Patch, "/fleet/workitems/wi-1", owner,
                   """{"auditMaxIterations":30,"prompt":"rewrite everything","state":"Done"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        }
        Assert.Equal("""{"auditMaxIterations":30}""", factory.Upstream.Seen[^1].Body);

        using (var empty = await SendAsync(http, HttpMethod.Patch, "/fleet/workitems/wi-1", owner, """{"prompt":"rewrite everything"}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }

        using (var answered = await SendAsync(http, HttpMethod.Post, "/fleet/workitems/wi-1/answer", owner,
                   """{"questionId":"q1","answer":"yes","actor":"root"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }
        Assert.Equal("""{"questionId":"q1","answer":"yes"}""", factory.Upstream.Seen[^1].Body);

        using (var cancelled = await SendAsync(http, HttpMethod.Delete, "/fleet/workitems/wi-1", owner))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }
        Assert.Equal(HttpMethod.Delete, factory.Upstream.Seen[^1].Method);
    }

    [Fact]
    public async Task Only_the_named_query_keys_pass_and_the_token_is_not_one_of_them()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (_, member) = await DevicesAsync(factory, http);

        using var response = await SendAsync(http, HttpMethod.Get, "/fleet/quota/history?agent=copilot&from=2026-09-01&limit=500&raw=1", member);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/quota/history?agent=copilot&from=2026-09-01&limit=500", factory.Upstream.Seen.Single().Path);

        // The hub's token query parameter authenticates but is never forwarded either.
        using var byQuery = await http.GetAsync($"/fleet/workitems?{WireProtocol.TokenParameter}={member}");
        Assert.Equal(HttpStatusCode.OK, byQuery.StatusCode);
        Assert.Equal("/workitems", factory.Upstream.Seen[^1].Path);
    }

    [Fact]
    public async Task No_token_no_fleet_and_no_orchestrator_no_capability()
    {
        using (var factory = new HostFactory())
        using (var http = factory.CreateClient())
        {
            using var response = await SendAsync(http, HttpMethod.Get, "/fleet/workitems", token: null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(factory.Upstream.Seen);
        }

        using (var factory = new HostFactory { WithFleet = false })
        using (var http = factory.CreateClient())
        {
            var (owner, _) = await DevicesAsync(factory, http);
            using var response = await SendAsync(http, HttpMethod.Get, "/fleet/workitems", owner);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            await using var client = new AgnesClient();
            var host = await client.AddHostAsync("http://localhost", owner, o =>
            {
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            });
            var caps = await host.GetCapabilitiesAsync();
            Assert.Contains(caps, c => c.Id == HostCapabilityIds.Fleet && !c.Available);
        }
    }
}
