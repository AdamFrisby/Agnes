using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
/// Device roles over the real endpoints: who a device is on this host, who may change that, and what a
/// Member can actually do.
///
/// <para>The rule these replace failed in a way worth restating, because it explains every choice here:
/// ownership used to be inferred from pairing ORDER, so the first record ever written to the store owned the
/// host — on this machine that turned out to be a test fixture from months earlier, and under the default
/// shared isolation the operator's own devices were refused every session, including ones they had just
/// opened. Ownership is now stated on the record, decided by how the device was admitted; a Member is a
/// first-class user of its own sessions; and nothing can leave the host with no Owner at all.</para>
/// </summary>
public sealed class DeviceRoleEndpointTests
{
    private const string BootstrapToken = "role-bootstrap";

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly Agnes.TestKit.IsolatedHostHome _home = new();

        public EndToEndTests.ScriptedAdapter Adapter { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:Home"] = _home.Path,
                    ["Agnes:PairingToken"] = BootstrapToken,
                    // A QR grant is only minted for a host that can say where it is.
                    ["Agnes:PublicUrl"] = "http://localhost",
                    // These tests walk several full pairings in one minute from one address, which the
                    // default auth throttle (rightly) treats as an attack. Raise it rather than sleep.
                    ["Agnes:Auth:RateLimit:PerIpPerMinute"] = "1000",
                    ["Agnes:Auth:RateLimit:GlobalPerMinute"] = "10000",
                }));
            builder.ConfigureServices(services => services.AddSingleton<IAgentAdapter>(Adapter));
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _home.Dispose();
            }
        }
    }

    // ---- who am I on this host ----

    [Fact]
    public async Task A_device_can_ask_what_it_is_without_being_shown_everybody_else()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, member) = await OwnerAndMemberAsync(factory, http);

        var asMember = await GetAsync<DeviceInfo>(http, "/devices/me", member.Token);
        Assert.Equal(member.DeviceId, asMember!.Id);
        Assert.Equal(DeviceRole.Member, asMember.Role);
        Assert.Equal("pairing-grant", asMember.Kind);       // how it got in, not merely who it is
        Assert.True(asMember.IsCurrentDevice);

        var asOwner = await GetAsync<DeviceInfo>(http, "/devices/me", owner.Token);
        Assert.Equal(DeviceRole.Owner, asOwner!.Role);

        // The list carries the same two facts, so the Devices page can explain an empty session list.
        var listed = await GetAsync<List<DeviceInfo>>(http, "/devices", owner.Token);
        Assert.Equal(2, listed!.Count);
        Assert.Single(listed, d => d.Role == DeviceRole.Owner);
    }

    [Fact]
    public async Task Devices_me_is_shut_to_an_unknown_token()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/devices/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- promoting, demoting, pruning ----

    [Fact]
    public async Task Only_an_owner_may_change_roles_and_never_away_from_the_last_one()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, member) = await OwnerAndMemberAsync(factory, http);

        // A Member cannot promote itself. This is the whole point of the role existing.
        Assert.Equal(HttpStatusCode.Forbidden, await SetRoleAsync(http, member.Token, member.DeviceId, DeviceRole.Owner));

        // The last Owner cannot demote itself — that would leave a host nobody can administer.
        Assert.Equal(HttpStatusCode.Conflict, await SetRoleAsync(http, owner.Token, owner.DeviceId, DeviceRole.Member));

        // An Owner promoting somebody else, then standing down, is fine.
        Assert.Equal(HttpStatusCode.OK, await SetRoleAsync(http, owner.Token, member.DeviceId, DeviceRole.Owner));
        Assert.Equal(HttpStatusCode.OK, await SetRoleAsync(http, owner.Token, owner.DeviceId, DeviceRole.Member));
        Assert.Equal(DeviceRole.Member, (await GetAsync<DeviceInfo>(http, "/devices/me", owner.Token))!.Role);
    }

    [Fact]
    public async Task Pruning_is_owner_only_and_spares_devices_still_in_use()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, member) = await OwnerAndMemberAsync(factory, http);

        using var refused = await PostAsync(http, "/devices/prune", member.Token, new DevicePruneRequest(30));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var pruned = await PostAsync(http, "/devices/prune", owner.Token, new DevicePruneRequest(30));
        pruned.EnsureSuccessStatusCode();
        // Both devices were minted seconds ago, so a 30-day sweep must take neither.
        Assert.Empty((await pruned.Content.ReadFromJsonAsync<List<DeviceInfo>>())!);
        Assert.Equal(2, (await GetAsync<List<DeviceInfo>>(http, "/devices", owner.Token))!.Count);
    }

    // ---- an approval hands over no more than the approver holds ----

    [Fact]
    public async Task A_member_cannot_approve_a_new_device_as_an_owner()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, member) = await OwnerAndMemberAsync(factory, http);

        // The Member vouches for a phone and asks for Owner. Vouching is not promotion.
        var admittedByMember = await ApproveAsync(http, member.Token, DeviceRole.Owner);
        Assert.Equal(DeviceRole.Member, admittedByMember.Role);

        // The Owner asking for the same thing gets it.
        var admittedByOwner = await ApproveAsync(http, owner.Token, DeviceRole.Owner);
        Assert.Equal(DeviceRole.Owner, admittedByOwner.Role);

        // And an approval with no body at all — what a client that predates roles sends — still works.
        var admittedByDefault = await ApproveAsync(http, owner.Token, role: null);
        Assert.Equal(DeviceRole.Member, admittedByDefault.Role);
    }

    // ---- what a Member can actually do ----

    [Fact]
    public async Task A_member_sees_the_session_it_started_but_not_the_owners()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, member) = await OwnerAndMemberAsync(factory, http);

        await using var ownerClient = new AgnesClient();
        var ownerHost = await ownerClient.AddHostAsync("http://owner", owner.Token, UseTestServer(factory));
        var ownersSession = await ownerHost.OpenSessionAsync("scripted", ".");

        await using var memberClient = new AgnesClient();
        var memberHost = await memberClient.AddHostAsync("http://member", member.Token, UseTestServer(factory));

        // A Member opens sessions like anybody else...
        var mine = await memberHost.OpenSessionAsync("scripted", ".");

        // ...and can watch what it started. Without this, a Member is refused the session it just opened —
        // the exact symptom that made this host unusable for every device but one.
        await memberHost.SubscribeAsync(mine.SessionId);
        var visible = await memberHost.ListSessionsAsync();
        Assert.Contains(visible, s => s.SessionId == mine.SessionId);

        // But somebody else's session is neither listed nor reachable.
        Assert.DoesNotContain(visible, s => s.SessionId == ownersSession.SessionId);
        await Assert.ThrowsAnyAsync<Exception>(() => memberHost.SubscribeAsync(ownersSession.SessionId));
    }

    [Fact]
    public async Task Opening_a_session_needs_a_paired_device()
    {
        using var factory = new HostFactory();
        using var http = factory.CreateClient();
        var (owner, member) = await OwnerAndMemberAsync(factory, http);

        await using var client = new AgnesClient();
        var host = await client.AddHostAsync("http://member", member.Token, UseTestServer(factory));

        // Revoked mid-connection: the token still opens a socket that was already established, so the check
        // has to be on the call, not on the handshake. An unattributable session is one only a host Owner
        // could ever see again — better to refuse than to strand it.
        using var revoke = new HttpRequestMessage(HttpMethod.Delete, "/devices/" + member.DeviceId);
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        (await http.SendAsync(revoke)).EnsureSuccessStatusCode();

        await Assert.ThrowsAnyAsync<Exception>(() => host.OpenSessionAsync("scripted", "."));
    }

    // ---- helpers ----

    private static Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions> UseTestServer(HostFactory factory)
        => options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        };

    /// <summary>
    /// The real pairing sequence: the operator types the code the host printed (admitted as an Owner, because
    /// that code is the operator's own secret), the typed code then closes, and a second device joins on a QR
    /// grant from the first. An Owner's grant hands over Owner, so the second device is demoted to the Member
    /// this test is about.
    /// </summary>
    private static async Task<(PairResponse Owner, PairResponse Member)> OwnerAndMemberAsync(
        HostFactory factory, HttpClient http)
    {
        var code = factory.Services.GetRequiredService<Agnes.Host.Hosting.DeviceRegistry>().PairingCode;
        var owner = await PairAsync(http, code, "owner-laptop");
        Assert.Equal(DeviceRole.Owner, owner.Role);

        var grant = (await PairingManagement.MintGrantAsync("http://localhost", owner.Token, httpClient: http))!;
        var member = await PairAsync(http, grant.Secret, "the-phone");
        Assert.Equal(HttpStatusCode.OK, await SetRoleAsync(http, owner.Token, member.DeviceId, DeviceRole.Member));
        return (owner, member);
    }

    private static async Task<PairResponse> PairAsync(HttpClient http, string code, string deviceName)
    {
        var response = await http.PostAsJsonAsync("/pair", new PairRequest(code, deviceName));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairResponse>())!;
    }

    /// <summary>Runs one full approval and returns the device the host admitted, as it sees itself.</summary>
    private static async Task<DeviceInfo> ApproveAsync(HttpClient http, string approverToken, DeviceRole? role)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pending = await PairingApproval.RequestAsync(
            "http://localhost", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "a-phone", http);

        using var approve = new HttpRequestMessage(HttpMethod.Post, "/pair/approve/" + pending.RequestId);
        approve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", approverToken);
        if (role is { } requested)
        {
            approve.Content = JsonContent.Create(new PairApprovalDecision(requested));
        }

        (await http.SendAsync(approve)).EnsureSuccessStatusCode();

        var polled = await PairingApproval.PollAsync("http://localhost", pending.RequestId, http);
        Assert.Equal(PairApprovalState.Approved, polled.State);
        return (await GetAsync<DeviceInfo>(http, "/devices/me", polled.Token!))!;
    }

    private static async Task<T?> GetAsync<T>(HttpClient http, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient http, string path, string token, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    private static async Task<HttpStatusCode> SetRoleAsync(HttpClient http, string token, string deviceId, DeviceRole role)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/devices/{deviceId}/role")
        {
            Content = JsonContent.Create(new DeviceRoleRequest(role)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }
}
