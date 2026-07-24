using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Host.Hosting;
using Agnes.Relay;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// End-to-end over the blind Agnes relay (spec AC2): an in-process relay broker sits between a real Kestrel
/// host (exposed via <see cref="AgnesRelayTransportProvider"/>) and an <see cref="AgnesClient"/> using the
/// relay transport. Proves real Agnes traffic (a prompt) round-trips through the tunnel, the client pins the
/// host's self-signed cert (wrong pin rejected), and the per-device bearer token is unchanged inside the
/// tunnel (AC4). TLS terminates at Kestrel, so the relay only ever forwards opaque bytes. No external network.
/// </summary>
public sealed class RelayEndToEndTests
{
    private const string Token = "relay-test-token";

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task Prompt_round_trips_through_the_relay_with_a_pinned_cert_and_bearer_token()
    {
        using var host = new RelayHost();
        await host.StartAsync();

        host.Adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("through the relay")));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        await using var client = new AgnesClient();
        var connected = await client.AddHostAsync(host.RelayAddress(host.Fingerprint), Token);

        // Real Agnes traffic over the tunnel: host info, open, subscribe, prompt, streamed events.
        // (info.HostId is the host's internal identity GUID, distinct from the relay routing host-id.)
        var info = await connected.GetHostInfoAsync();
        Assert.False(string.IsNullOrEmpty(info.HostId));

        var agents = await connected.ListAgentsAsync();
        Assert.Contains(agents, a => a.AdapterId == "scripted");

        var session = await connected.OpenSessionAsync("scripted", ".");
        var view = await connected.SubscribeAsync(session.SessionId);
        await connected.PromptAsync(session.SessionId, [new TextContent("hello over relay")]);

        await WaitForAsync(() => view.Events.OfType<TurnEndedEvent>().Any());
        Assert.Contains(view.Events, e => e is MessageChunkEvent { Role: MessageRole.Assistant });
    }

    [Fact]
    public async Task Client_with_a_wrong_pinned_fingerprint_is_rejected()
    {
        using var host = new RelayHost();
        await host.StartAsync();

        await using var client = new AgnesClient();
        string wrongPin = new string('a', 64); // a valid-looking but non-matching SHA-256 pin.

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.AddHostAsync(host.RelayAddress(wrongPin), Token));
    }

    [Fact]
    public async Task Bearer_token_is_still_enforced_inside_the_tunnel()
    {
        using var host = new RelayHost();
        await host.StartAsync();

        await using var client = new AgnesClient();
        // Correct cert pin, wrong token: the tunnel carries the handshake to Kestrel, which rejects the token
        // exactly as on the direct path (AC4 — switching to relay doesn't change auth).
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.AddHostAsync(host.RelayAddress(host.Fingerprint), "wrong-token"));
    }

    /// <summary>
    /// A real Kestrel Agnes host fronted by an in-process relay. Uses the documented "Kestrel behind
    /// WebApplicationFactory" pattern (a real listener, not the in-memory TestServer) because the host's blind
    /// pump forwards to Kestrel's HTTPS port over a real loopback socket.
    /// </summary>
    private sealed class RelayHost : WebApplicationFactory<Program>
    {
        private readonly InMemoryRelayHostKey _relayKey = new();
        private readonly RelayServer _relay;
        private readonly string _certPath = Path.Combine(Path.GetTempPath(), $"agnes-relay-cert-{Guid.NewGuid():n}.pfx");
        private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"agnes-relay-key-{Guid.NewGuid():n}.pem");
        private readonly SelfSignedHostCertificateProvider _cert;
        private IHost? _kestrelHost;
        private bool _disposed;

        public RelayHost()
        {
            // The relay authorizes THIS host's key only; start it up front so the host can register on startup.
            _relay = new RelayServer(
                new RelayOptions { ListenAddress = "127.0.0.1", Port = 0 },
                new InMemoryAuthorizedHostKeys([_relayKey.Spki]));
            _relay.Start();
            _cert = new SelfSignedHostCertificateProvider(_certPath);
        }

        public ScriptedAdapter Adapter { get; } = new();

        public string HostId { get; } = "relay-host-" + Guid.NewGuid().ToString("n");

        public int RelayPort => _relay.Port;

        public string Fingerprint { get; private set; } = "";

        /// <summary>Triggers host build + Kestrel start + relay registration.</summary>
        public async Task StartAsync()
        {
            _ = Services; // forces CreateHost (starts Kestrel, exposes via the relay).
            await Task.CompletedTask;
        }

        public string RelayAddress(string fingerprint)
            => $"agnes-relay://127.0.0.1:{RelayPort}/{HostId}?fp={fingerprint}";

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // NB: the transport stays 'direct' for BOTH host builds so Program's own startup exposure is a
            // harmless address-echo. The relay exposure is driven manually below against the Kestrel host only,
            // so the in-memory TestServer build (which has no real port) never tries to register on the relay.
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:PairingToken"] = Token,
                    ["Agnes:Transport:Relay:Url"] = $"127.0.0.1:{RelayPort}",
                    ["Agnes:Transport:Relay:HostId"] = HostId,
                    ["Agnes:Transport:Relay:CertFile"] = _certPath,
                    ["Agnes:Transport:Relay:KeyFile"] = _keyPath,
                }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAgentAdapter>(Adapter);
                // Use the key the relay authorized (Program would otherwise generate a fresh, unauthorized one)
                // and the same cert instance Kestrel presents, so the advertised fingerprint is what clients pin.
                services.AddSingleton<IRelayHostKey>(_relayKey);
                services.AddSingleton<IHostCertificateProvider>(_cert);
            });

            // "Kestrel behind WebApplicationFactory" pattern: WAF wants an in-memory TestServer host, but the
            // host's blind pump forwards to a REAL loopback socket, so the real host runs on Kestrel.
            var testHost = builder.Build();

            builder.ConfigureWebHost(webHost => webHost
                .UseKestrel()
                // appsettings.Development.json pins Kestrel to https://0.0.0.0:5081, and Kestrel's Endpoints
                // config wins over UseUrls — so the test bound 5081 and collided with any running dev host
                // (or a parallel test run) on that port. Override the endpoint to an ephemeral port; added
                // after the default appsettings sources, so it takes precedence.
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kestrel:Endpoints:Https:Url"] = "https://127.0.0.1:0",
                }))
                .ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
                    https.ServerCertificate = _cert.GetCertificate()))
                .UseUrls("https://127.0.0.1:0"));
            _kestrelHost = builder.Build();
            _kestrelHost.Start();

            string bound = _kestrelHost.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Fingerprint = _cert.Fingerprint;

            // Manually expose the relay transport for the Kestrel host (real port), registering it on the relay.
            var relayProvider = _kestrelHost.Services
                .GetRequiredService<Agnes.Abstractions.IPluginRegistry<ITransportProvider>>()
                .Find("agnes-relay")!;
            relayProvider.ExposeAsync(new HostExposureContext([bound])).GetAwaiter().GetResult();

            testHost.Start();
            return testHost;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing || _disposed)
            {
                return;
            }

            _disposed = true;
            _kestrelHost?.Dispose();
            _relay.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _relayKey.Dispose();
            _cert.Dispose();
            foreach (string path in (string[])[_certPath, _keyPath])
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    /// <summary>A scripted agent adapter standing in for a real CLI (mirrors the direct end-to-end fixture).</summary>
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

        public void Emit(SessionEvent e) => _events.Writer.TryWrite(e);

        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
            => OnPrompt(content, this);

        public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
