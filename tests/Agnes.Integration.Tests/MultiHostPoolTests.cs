using Agnes.Abstractions;
using Agnes.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agnes.Integration.Tests;

/// <summary>
/// The client connection pool holding several hosts at once over the real SignalR wire (multi-server support,
/// connectivity/02). Two independent in-memory hosts stand in for two servers; each keeps its own session list,
/// the pool surfaces the union tagged by host, and adding/removing/reconnecting one host never disturbs another.
/// Fully offline — each host is an in-process <see cref="WebApplicationFactory{Program}"/> TestServer.
/// </summary>
public sealed class MultiHostPoolTests
{
    private const string Token = "test-token";

    private static Action<HttpConnectionOptions> UseServer(WebApplicationFactory<Program> factory)
        => options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        };

    [Fact]
    public async Task Pool_holds_two_hosts_at_once_each_with_its_own_session_list()
    {
        using var serverA = new EndToEndTests.HostFactory();
        using var serverB = new EndToEndTests.HostFactory();

        await using var client = new AgnesClient();
        // Distinct pool keys; each handler routes to its own TestServer regardless of the URL host.
        var a = await client.AddHostAsync("http://host-a", Token, UseServer(serverA));
        var b = await client.AddHostAsync("http://host-b", Token, UseServer(serverB));

        Assert.Equal(2, client.Hosts.Count);
        Assert.Equal(AgnesConnectionState.Connected, a.State);
        Assert.Equal(AgnesConnectionState.Connected, b.State);
        Assert.NotEqual(a.HostId, b.HostId);

        // One session on A, two on B — each host tracks only its own.
        var sa = await a.OpenSessionAsync("scripted", ".");
        await a.SubscribeAsync(sa.SessionId);
        var sb1 = await b.OpenSessionAsync("scripted", ".");
        var sb2 = await b.OpenSessionAsync("scripted", ".");
        await b.SubscribeAsync(sb1.SessionId);
        await b.SubscribeAsync(sb2.SessionId);

        Assert.Single(a.Sessions);
        Assert.Equal(2, b.Sessions.Count);
    }

    [Fact]
    public async Task Aggregate_unions_sessions_across_hosts_each_tagged_with_its_host_id()
    {
        using var serverA = new EndToEndTests.HostFactory();
        using var serverB = new EndToEndTests.HostFactory();

        await using var client = new AgnesClient();
        var a = await client.AddHostAsync("http://host-a", Token, UseServer(serverA));
        var b = await client.AddHostAsync("http://host-b", Token, UseServer(serverB));

        var sa = await a.OpenSessionAsync("scripted", ".");
        await a.SubscribeAsync(sa.SessionId);
        var sb1 = await b.OpenSessionAsync("scripted", ".");
        var sb2 = await b.OpenSessionAsync("scripted", ".");
        await b.SubscribeAsync(sb1.SessionId);
        await b.SubscribeAsync(sb2.SessionId);

        var all = client.AllSessions;
        Assert.Equal(3, all.Count);
        Assert.Equal(1, all.Count(s => s.HostId == a.HostId));
        Assert.Equal(2, all.Count(s => s.HostId == b.HostId));
        Assert.All(all, s => Assert.Equal(ClientTransportKind.Direct, s.Transport));

        // The host-list surface reports each host's own state + session count.
        var statuses = client.HostStatuses;
        Assert.Equal(2, statuses.Count);
        Assert.Equal(1, statuses.Single(s => s.HostId == a.HostId).SessionCount);
        Assert.Equal(2, statuses.Single(s => s.HostId == b.HostId).SessionCount);
        Assert.All(statuses, s => Assert.Equal(AgnesConnectionState.Connected, s.State));
    }

    [Fact]
    public async Task Removing_one_host_leaves_the_other_live_with_its_sessions()
    {
        using var serverA = new EndToEndTests.HostFactory();
        using var serverB = new EndToEndTests.HostFactory();

        await using var client = new AgnesClient();
        var a = await client.AddHostAsync("http://host-a", Token, UseServer(serverA));
        var b = await client.AddHostAsync("http://host-b", Token, UseServer(serverB));
        var sa = await a.OpenSessionAsync("scripted", ".");
        await a.SubscribeAsync(sa.SessionId);
        var sb = await b.OpenSessionAsync("scripted", ".");
        await b.SubscribeAsync(sb.SessionId);

        await client.RemoveHostAsync("http://host-a");

        var host = Assert.Single(client.Hosts);
        Assert.Equal(b.HostId, host.HostId);
        Assert.Equal(AgnesConnectionState.Connected, b.State);
        // B is still fully usable, and the aggregate now only carries B's session.
        await b.PromptAsync(sb.SessionId, [new TextContent("still here")]);
        var remaining = Assert.Single(client.AllSessions);
        Assert.Equal(b.HostId, remaining.HostId);
    }

    [Fact]
    public async Task Dropping_and_reconnecting_one_host_never_disturbs_the_other()
    {
        using var serverA = new EndToEndTests.HostFactory();
        using var serverB = new EndToEndTests.HostFactory();

        await using var client = new AgnesClient();
        var a = await client.AddHostAsync("http://host-a", Token, UseServer(serverA));
        var b = await client.AddHostAsync("http://host-b", Token, UseServer(serverB));
        var sb = await b.OpenSessionAsync("scripted", ".");
        await b.SubscribeAsync(sb.SessionId);

        // Watch B for any connection disturbance while A drops and reconnects.
        var bStates = new List<AgnesConnectionState>();
        b.StateChanged += s => { lock (bStates) { bStates.Add(s); } };

        await client.RemoveHostAsync("http://host-a");                       // drop A
        var a2 = await client.AddHostAsync("http://host-a", Token, UseServer(serverA)); // reconnect A

        Assert.Equal(AgnesConnectionState.Connected, a2.State);
        Assert.Equal(AgnesConnectionState.Connected, b.State);
        lock (bStates)
        {
            Assert.DoesNotContain(AgnesConnectionState.Disconnected, bStates);
            Assert.DoesNotContain(AgnesConnectionState.Reconnecting, bStates);
        }

        // B's session list is untouched by A's churn.
        Assert.Single(b.Sessions);
        Assert.Single(client.AllSessions, s => s.HostId == b.HostId);
    }
}
