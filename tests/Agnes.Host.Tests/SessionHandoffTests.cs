using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Host.Sessions.Handoff;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>Cross-host session handoff (connectivity/03): the replay flow across two in-process hosts, the
/// HandoffSupport reporting, and the direct-first/relay-fallback channel selection.</summary>
public class SessionHandoffTests : IDisposable
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    // A fresh scripted session per launch, optionally advertising handoff support.
    private sealed class HandoffAdapter(HandoffSupport? support = null, string id = "scripted")
        : IAgentAdapter, IHandoffCapableAdapter
    {
        private readonly HandoffSupport _support = support ?? HandoffSupport.Replay;
        public bool IsCapable { get; } = support is not null;
        public List<ScriptedAgentSession> Sessions { get; } = [];
        public string? ExportedFrom { get; private set; }
        public AgentDescriptor Descriptor { get; } = new() { Id = id, DisplayName = "S" };

        HandoffSupport IHandoffCapableAdapter.Support => _support;

        Task<string> IHandoffCapableAdapter.ExportHandoffStateAsync(IAgentSession session, CancellationToken ct)
        {
            ExportedFrom = session.AgentSessionId;
            return Task.FromResult("native-token-" + session.AgentSessionId);
        }

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            var s = new ScriptedAgentSession();
            Sessions.Add(s);
            return Task.FromResult<IAgentSession>(s);
        }
    }

    // A capability-less adapter — the "Unsupported" case.
    private sealed class PlainAdapter : IAgentAdapter
    {
        public List<ScriptedAgentSession> Sessions { get; } = [];
        public AgentDescriptor Descriptor { get; } = new() { Id = "scripted", DisplayName = "S" };
        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            var s = new ScriptedAgentSession();
            Sessions.Add(s);
            return Task.FromResult<IAgentSession>(s);
        }
    }

    private readonly string _src = Path.Combine(Path.GetTempPath(), "agnes-handoff-src-" + Guid.NewGuid().ToString("n"));
    private readonly string _dst = Path.Combine(Path.GetTempPath(), "agnes-handoff-dst-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        foreach (var d in new[] { _src, _dst })
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) { cts.Token.ThrowIfCancellationRequested(); await Task.Delay(10, cts.Token); }
    }

    private static async Task WaitForEventAsync(SessionManager m, string sessionId, Func<IReadOnlyList<SessionEvent>, bool> pred)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (pred((await m.GetSnapshotAsync(sessionId, 0)).Events)) { return; }
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task Replay_handoff_seeds_the_source_transcript_into_a_new_session_on_a_second_host()
    {
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);

        // Two independent in-process hosts, each with its own adapter + event store.
        var sourceAdapter = new HandoffAdapter(HandoffSupport.Replay);
        await using var source = new SessionManager(
            TestPluginRegistries.Agents(sourceAdapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var targetAdapter = new HandoffAdapter(HandoffSupport.Replay);
        await using var target = new SessionManager(
            TestPluginRegistries.Agents(targetAdapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        // Build a multi-event transcript on the source host.
        var parent = await source.OpenSessionAsync("scripted", _src, useSandbox: false);
        sourceAdapter.Sessions[0].OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("hi there")));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };
        await source.PromptAsync(parent.SessionId, [new TextContent("hello")]);
        await WaitForEventAsync(source, parent.SessionId, e => e.OfType<TurnEndedEvent>().Any());

        // Prepare on the source, accept on the target.
        var state = await source.PrepareHandoffAsync(parent.SessionId);
        Assert.Equal(HandoffSupport.Replay, state.Mode);
        Assert.NotEmpty(state.SeedEvents);

        var accepted = await target.AcceptHandoffAsync(state, _dst);

        // The child's log opens with a ForkedFromEvent naming the source session — the identity link.
        var childSnap = await target.GetSnapshotAsync(accepted.SessionId, 0);
        var marker = Assert.Single(childSnap.Events.OfType<ForkedFromEvent>());
        Assert.Equal(parent.SessionId, marker.ParentSessionId);

        // The new session's agent receives the reconstructed transcript ahead of the first prompt.
        IReadOnlyList<ContentBlock>? childGot = null;
        targetAdapter.Sessions[0].OnPrompt = (c, s) =>
        {
            childGot = c;
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };
        await target.PromptAsync(accepted.SessionId, [new TextContent("continue")]);
        await WaitForAsync(() => childGot is not null);

        var seededText = string.Concat(childGot!.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("Forked conversation", seededText);
        Assert.Contains("hello", seededText);     // source user message
        Assert.Contains("hi there", seededText);  // source assistant message
        Assert.Contains("continue", seededText);  // the real new prompt, appended after the seed
    }

    [Fact]
    public async Task HandoffSupportFor_reports_Unsupported_without_the_capability_and_Replay_with_it()
    {
        var plain = new PlainAdapter();
        await using var plainManager = new SessionManager(
            TestPluginRegistries.Agents(plain), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);
        Assert.Equal(HandoffSupport.Unsupported, plainManager.HandoffSupportFor("scripted"));

        var capable = new HandoffAdapter(HandoffSupport.Replay);
        await using var capableManager = new SessionManager(
            TestPluginRegistries.Agents(capable), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);
        Assert.Equal(HandoffSupport.Replay, capableManager.HandoffSupportFor("scripted"));
    }

    [Fact]
    public async Task PrepareHandoff_refuses_an_unsupported_agent_with_a_typed_error()
    {
        Directory.CreateDirectory(_src);
        var plain = new PlainAdapter();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(plain), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var session = await manager.OpenSessionAsync("scripted", _src, useSandbox: false);

        await Assert.ThrowsAsync<HandoffNotSupportedException>(() => manager.PrepareHandoffAsync(session.SessionId));
    }

    // ---- Channel selection (direct-first, relay-fallback) --------------------------------------------------

    private sealed class StubProbe(bool reachable) : IHostReachabilityProbe
    {
        public Task<bool> IsDirectlyReachableAsync(HostEndpoint target, CancellationToken ct = default)
            => Task.FromResult(reachable);
    }

    private sealed class RecordingFactory : IHandoffChannelFactory
    {
        public int DirectCalls { get; private set; }
        public int RelayCalls { get; private set; }

        public Task<IHandoffChannel> OpenDirectAsync(HostEndpoint target, CancellationToken ct = default)
        {
            DirectCalls++;
            return Task.FromResult<IHandoffChannel>(new DirectHandoffChannel(new MemoryStream()));
        }

        public Task<IHandoffChannel> OpenRelayAsync(HostEndpoint target, CancellationToken ct = default)
        {
            RelayCalls++;
            return Task.FromResult<IHandoffChannel>(new RelayHandoffChannel(new MemoryStream()));
        }
    }

    [Fact]
    public async Task Channel_selection_takes_the_direct_route_when_the_target_is_reachable()
    {
        var factory = new RecordingFactory();
        var selector = new HandoffChannelSelector(new StubProbe(reachable: true), factory);

        await using var channel = await selector.OpenAsync(new HostEndpoint("host-b", "10.0.0.5:7777"));

        Assert.Equal(HandoffChannelKind.Direct, channel.Kind);
        Assert.Equal(1, factory.DirectCalls);
        Assert.Equal(0, factory.RelayCalls);
    }

    [Fact]
    public async Task Channel_selection_falls_back_to_the_relay_when_the_target_is_unreachable()
    {
        var factory = new RecordingFactory();
        var selector = new HandoffChannelSelector(new StubProbe(reachable: false), factory);

        await using var channel = await selector.OpenAsync(new HostEndpoint("host-b", "agnes-relay://relay:9000/host-b"));

        Assert.Equal(HandoffChannelKind.Relay, channel.Kind);
        Assert.Equal(0, factory.DirectCalls);
        Assert.Equal(1, factory.RelayCalls);
    }
}
