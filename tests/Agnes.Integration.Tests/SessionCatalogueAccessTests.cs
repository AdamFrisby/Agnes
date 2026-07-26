using System.Net.Http.Json;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// The security claim behind the session catalogue: <c>ListSessions</c> advertises exactly what the caller
/// could already subscribe to, no more. Proven over the real wire with two genuinely different callers — the
/// host owner, and a later-paired device with no share — because a list that leaks is worse than no list: it
/// names other people's projects and folders to someone who cannot open them.
///
/// <para>This also pins the flip side, which is a real product consequence rather than a bug in the list: on a
/// stock host (<c>SessionIsolation=Shared</c>) a second device sees an empty catalogue until the owner shares
/// a session with it, because that is precisely what it may subscribe to. Change the sharing policy and this
/// list follows it — the gate is the same one, asked once per row.</para>
/// </summary>
public class SessionCatalogueAccessTests : IClassFixture<SessionCatalogueAccessTests.HostFactory>
{
    private const string BootstrapToken = "catalogue-bootstrap";
    private readonly HostFactory _factory;

    public SessionCatalogueAccessTests(HostFactory factory) => _factory = factory;

    private Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions> UseTestServer()
        => options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
        };

    [Fact]
    public async Task A_device_that_may_not_subscribe_is_not_told_the_session_exists()
    {
        using var http = _factory.CreateClient();

        // Two real devices: the first to pair is the host owner, the second is just another paired device.
        // The typed code is minted by the host at startup (and closes once a device has paired), so the
        // second device is vouched for by a QR grant from the first — exactly the real pairing sequence.
        var code = _factory.Services.GetRequiredService<Agnes.Host.Hosting.DeviceRegistry>().PairingCode;
        var owner = await PairAsync(http, code, "owner-laptop");
        var grant = await MintGrantAsync(http, owner.Token);
        var other = await PairAsync(http, grant.Secret, "someone-elses-phone");
        Assert.NotEqual(owner.DeviceId, other.DeviceId);

        // The owner opens a session; nothing is shared with the other device.
        await using var ownerClient = new AgnesClient();
        var ownerHost = await ownerClient.AddHostAsync("http://localhost", owner.Token, UseTestServer());
        var session = await ownerHost.OpenSessionAsync("scripted", ".");

        // The owner sees it in the catalogue...
        var mine = await ownerHost.ListSessionsAsync();
        Assert.Contains(mine, s => s.SessionId == session.SessionId);

        // ...and the unshared device is told nothing at all — not the id, not the folder.
        await using var otherClient = new AgnesClient();
        var otherHost = await otherClient.AddHostAsync("http://localhost", other.Token, UseTestServer());
        var theirs = await otherHost.ListSessionsAsync();
        Assert.DoesNotContain(theirs, s => s.SessionId == session.SessionId);

        // Which is the same answer subscribing gives: the list mirrors the gate rather than second-guessing it.
        await Assert.ThrowsAnyAsync<Exception>(() => otherHost.SubscribeAsync(session.SessionId));
    }

    private static async Task<PairResponse> PairAsync(HttpClient http, string code, string deviceName)
    {
        var response = await http.PostAsJsonAsync("/pair", new PairRequest(code, deviceName));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairResponse>())!;
    }

    private static async Task<PairingGrant> MintGrantAsync(HttpClient http, string token)
    {
        var grant = await PairingManagement.MintGrantAsync("http://localhost", token, httpClient: http);
        Assert.NotNull(grant);
        return grant!;
    }

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        public EndToEndTests.ScriptedAdapter Adapter { get; } = new();

        // Throwaway device/mcp state so the test never touches the real ~/.agnes/*, and never inherits a
        // device list from another test run (which would decide who the "owner" is behind our backs).
        public string DeviceFile { get; } = Path.Combine(Path.GetTempPath(), $"agnes-devices-cat-{Guid.NewGuid():n}.json");
        public string McpFile { get; } = Path.Combine(Path.GetTempPath(), $"agnes-mcp-cat-{Guid.NewGuid():n}.json");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:PairingToken"] = BootstrapToken,
                    ["Agnes:DevicesFile"] = DeviceFile,
                    ["Agnes:McpFile"] = McpFile,
                    // A QR grant is only minted for a host that can say where it is; under the test server
                    // there is no real address to discover, so state one.
                    ["Agnes:PublicUrl"] = "http://localhost",
                }));
            builder.ConfigureServices(services => services.AddSingleton<IAgentAdapter>(Adapter));
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(DeviceFile)) File.Delete(DeviceFile);
            if (disposing && File.Exists(McpFile)) File.Delete(McpFile);
        }
    }
}
