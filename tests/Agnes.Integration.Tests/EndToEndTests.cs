using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// End-to-end walking skeleton over the real SignalR wire: Agnes.Client ⇄ AgnesHub ⇄
/// SessionManager ⇄ agent (scripted, standing in for Claude Code). Proves pairing, agent
/// listing, session open, prompt, streamed events, permission round-trip, and multi-client replay.
/// </summary>
public class EndToEndTests : IClassFixture<EndToEndTests.HostFactory>
{
    private const string Token = "test-token";
    private readonly HostFactory _factory;

    public EndToEndTests(HostFactory factory) => _factory = factory;

    private Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions> UseTestServer()
        => options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
        };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task Full_slice_pairs_opens_prompts_streams_and_replays_to_second_client()
    {
        _factory.Adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("Working on it")));
            s.Emit(new PermissionRequestedEvent("req1", "tc1", "Run a command",
                [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce)]));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        await using var client = new AgnesClient();
        var host = await client.AddHostAsync("http://localhost", Token, UseTestServer());

        // Pairing + agent discovery over the wire.
        var info = await host.GetHostInfoAsync();
        Assert.False(string.IsNullOrEmpty(info.HostId));
        var agents = await host.ListAgentsAsync();
        Assert.Contains(agents, a => a.AdapterId == "scripted");

        // Open + subscribe + prompt.
        var session = await host.OpenSessionAsync("scripted", ".");
        var view = await host.SubscribeAsync(session.SessionId);
        await host.PromptAsync(session.SessionId, [new TextContent("hello")]);

        // Streamed events arrive: user echo, assistant message, permission request.
        await WaitForAsync(() => view.Events.OfType<PermissionRequestedEvent>().Any());
        Assert.Contains(view.Events, e => e is MessageChunkEvent { Role: MessageRole.User });
        Assert.Contains(view.Events, e => e is MessageChunkEvent { Role: MessageRole.Assistant });
        var permission = view.Events.OfType<PermissionRequestedEvent>().Single();
        Assert.Equal("req1", permission.RequestId);

        // Permission response round-trips without error.
        await host.RespondPermissionAsync(session.SessionId, permission.RequestId, "allow");

        // Multi-client proof: a second client replays the full history via snapshot.
        await using var client2 = new AgnesClient();
        var host2 = await client2.AddHostAsync("http://localhost", Token, UseTestServer());
        var view2 = await host2.SubscribeAsync(session.SessionId);
        Assert.True(view2.Events.Count >= 3);
        Assert.Contains(view2.Events, e => e is MessageChunkEvent { Role: MessageRole.Assistant });
    }

    [Fact]
    public async Task Cancel_flows_over_the_wire_to_the_agent_session()
    {
        _factory.Adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("thinking…")));
            return Task.FromResult(StopReason.EndTurn);
        };

        await using var client = new AgnesClient();
        var host = await client.AddHostAsync("http://localhost", Token, UseTestServer());
        var session = await host.OpenSessionAsync("scripted", ".");
        await host.SubscribeAsync(session.SessionId);
        await host.PromptAsync(session.SessionId, [new TextContent("go")]);

        await host.CancelAsync(session.SessionId);

        // The Stop reaches the agent session's CancelAsync over the SignalR wire.
        await _factory.Adapter.Session.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(_factory.Adapter.Session.Cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SetMode_flows_over_the_wire_to_the_agent_session()
    {
        _factory.Adapter.Session.OnPrompt = (_, _) => Task.FromResult(StopReason.EndTurn);

        await using var client = new AgnesClient();
        var host = await client.AddHostAsync("http://localhost", Token, UseTestServer());
        var session = await host.OpenSessionAsync("scripted", ".");

        await host.SetModeAsync(session.SessionId, "plan");

        var mode = await _factory.Adapter.Session.ModeSet.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("plan", mode);
    }

    [Fact]
    public async Task Rejects_connection_without_valid_token()
    {
        await using var client = new AgnesClient();
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.AddHostAsync("http://localhost", "wrong-token", UseTestServer()));
    }

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        public ScriptedAdapter Adapter { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Agnes:PairingToken"] = Token }));
            builder.ConfigureServices(services => services.AddSingleton<IAgentAdapter>(Adapter));
            return base.CreateHost(builder);
        }
    }

    public sealed class ScriptedAdapter : IAgentAdapter
    {
        public ScriptedSession Session { get; } = new();

        public AgentDescriptor Descriptor { get; } = new() { Id = "scripted", DisplayName = "Scripted" };

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(Session);
    }

    public sealed class ScriptedSession : IAgentSession
    {
        private readonly Channel<SessionEvent> _events =
            Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });

        public string AgentSessionId => "scripted";
        public ChannelReader<SessionEvent> Events => _events.Reader;
        public Func<IReadOnlyList<ContentBlock>, ScriptedSession, Task<StopReason>> OnPrompt { get; set; }
            = (_, _) => Task.FromResult(StopReason.EndTurn);

        public TaskCompletionSource Cancelled { get; } = new();
        public TaskCompletionSource<string> ModeSet { get; } = new();

        public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
        {
            ModeSet.TrySetResult(modeId);
            return Task.CompletedTask;
        }

        public void Emit(SessionEvent e) => _events.Writer.TryWrite(e);
        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
            => OnPrompt(content, this);
        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            Cancelled.TrySetResult();
            return Task.CompletedTask;
        }
        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
