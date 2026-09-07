using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agnes.Host.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Tests.Mcp;

/// <summary>
/// The loopback MCP listener, end to end over real HTTP: an unsandboxed agent holding nothing but its
/// session's bearer token must be able to <c>initialize</c> and see <c>send_user_file</c> in
/// <c>tools/list</c> — and the same plaintext port must serve nothing else.
/// </summary>
/// <remarks>
/// This stands the endpoint up the way <c>Program</c> does (stateless streamable-HTTP transport, the
/// path gate first in the pipeline, plaintext on loopback) rather than mocking it, because every failure
/// this guards against is a wiring failure: a gate that lets the hub through, a transport that rejects a
/// session token, an endpoint mapped at a path the generated configs don't name.
/// </remarks>
public sealed class LocalMcpListenerTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private readonly SessionMcpTokens _tokens = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton<IAgnesMcpBackend>(new FakeAgnesMcpBackend());
        builder.Services.AddSingleton<IMcpDeviceAuthenticator>(new FakeMcpAuthenticator("device-token"));
        builder.Services.AddSingleton<IMcpCallerTokenSource, HttpContextMcpTokenSource>();
        builder.Services.AddSingleton(Agnes.Host.Tests.Display.DisplayFixture.NoDisplays());
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<AgnesMcpTools>();

        _app = builder.Build();

        // Exactly the gate Program installs, first in the pipeline. The port is only known after binding,
        // so the set is filled below — the middleware reads the same HashSet either way.
        var restricted = new HashSet<int>();
        _app.Use(async (ctx, next) =>
        {
            if (restricted.Contains(ctx.Connection.LocalPort)
                && !GuestMcpEndpoint.IsAllowedPath(ctx.Request.Path.Value))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
        _app.MapGet("/agnes", () => "the hub would be here"); // must never be reachable on this port
        _app.MapMcp(AgnesMcpEndpoints.Path);

        await _app.StartAsync();
        var address = BaseUrl();
        restricted.UnionWith(GuestMcpEndpoint.RestrictedPorts(address));
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private string BaseUrl()
        => _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

    /// <summary>One JSON-RPC call over the streamable-HTTP transport, returning the parsed response.</summary>
    private async Task<(HttpStatusCode Status, JsonElement? Body)> CallAsync(string method, string? bearer, object? parameters = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = parameters ?? new { },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, AgnesMcpEndpoints.Path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, ParseRpc(body));
    }

    // The transport answers as JSON or as a one-event SSE stream depending on negotiation; both carry the
    // same JSON-RPC envelope.
    private static JsonElement? ParseRpc(string body)
    {
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?["data:".Length..];

        return json is { Length: > 0 } ? JsonDocument.Parse(json).RootElement.Clone() : null;
    }

    private async Task<string> InitializedSessionTokenAsync(string sessionId)
    {
        var token = _tokens.Issue(sessionId);
        var (status, body) = await CallAsync("initialize", token, new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "agnes-test", version = "1.0" },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!.Value.TryGetProperty("result", out _));
        return token;
    }

    [Fact]
    public async Task An_agent_holding_only_its_session_token_can_initialize_and_list_the_agnes_tools()
    {
        var token = await InitializedSessionTokenAsync("sess-1");

        var (status, body) = await CallAsync("tools/list", token);

        Assert.Equal(HttpStatusCode.OK, status);
        var names = body!.Value.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();

        Assert.Contains("send_user_file", names);
        Assert.Contains("arm_goal", names);
    }

    [Fact]
    public async Task A_revoked_session_token_no_longer_names_a_session()
    {
        var token = await InitializedSessionTokenAsync("sess-2");
        Assert.Equal("sess-2", _tokens.SessionFor(token));

        _tokens.Revoke("sess-2");

        // The config file a closed session left behind now authenticates as nobody: the tool layer resolves
        // neither a session (SessionFor) nor a device (the token was never a device token).
        Assert.Null(_tokens.SessionFor(token));
        var tools = new AgnesMcpTools(
            new FakeAgnesMcpBackend(), new FakeMcpAuthenticator("device-token"), new FixedTokenSource(token), _tokens, Agnes.Host.Tests.Display.DisplayFixture.NoDisplays());
        await Assert.ThrowsAsync<McpUnauthenticatedException>(() => tools.SendUserFile("out.txt", null, null));
    }

    // ---- the outer wall in front of the endpoint ----

    [Fact]
    public void The_endpoint_wall_accepts_a_session_token_as_well_as_a_device_token()
    {
        // Accepting only device tokens 401s every agent before it reaches the tools — which is what made the
        // agnes server unreachable from the sessions its config was being written into.
        var deviceFile = Path.Combine(Path.GetTempPath(), $"agnes-devices-{Guid.NewGuid():n}.json");
        var devices = new Agnes.Host.Hosting.DeviceRegistry("device-token", deviceFile);
        var sessions = new SessionMcpTokens();
        var sessionToken = sessions.Issue("sess-wall");

        Assert.True(McpEndpointGate.IsAccepted("device-token", devices, sessions));
        Assert.True(McpEndpointGate.IsAccepted(sessionToken, devices, sessions));
        Assert.False(McpEndpointGate.IsAccepted("neither-of-those", devices, sessions));
        Assert.False(McpEndpointGate.IsAccepted(null, devices, sessions));

        // And a closed session's config stops getting through the wall at all.
        sessions.Revoke("sess-wall");
        Assert.False(McpEndpointGate.IsAccepted(sessionToken, devices, sessions));

        File.Delete(deviceFile);
    }

    [Fact]
    public async Task The_plaintext_port_serves_nothing_but_the_mcp_path()
    {
        // The hub, the REST API, the web head and the display channel all carry device tokens; on a port with
        // no TLS and no device authentication they must not exist at all. The display path especially: an
        // unauthenticated plaintext socket onto a session's screen is precisely what this allowlist prevents.
        foreach (var path in new[] { "/agnes", "/", "/devices", Agnes.Protocol.DisplayWire.Path + "/sess-1" })
        {
            using var response = await _http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
