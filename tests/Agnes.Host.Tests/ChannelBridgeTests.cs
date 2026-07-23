using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Channels;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Channel-bridge core (extensibility/04): outbound triggering off the event spine, an explicit
/// chat-id↔identity linking step, and inbound authorization that funnels an approval through the same
/// permission path a paired client uses. A <see cref="FakeChannelBridge"/> proves the round-trip; no real
/// network transport is involved.
/// </summary>
public sealed class ChannelBridgeTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    /// <summary>The full host-side wiring for a set of bridges, sharing one event bus with the SessionManager.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required SessionManager Manager { get; init; }
        public required ScriptedAgentAdapter Adapter { get; init; }
        public required IEventStore Store { get; init; }
        public required ChannelLinkStore Links { get; init; }
        public required ChannelBridgeNotifier Notifier { get; init; }
        public required ChannelBridgeRouter Router { get; init; }

        public async ValueTask DisposeAsync()
        {
            Router.Dispose();
            Notifier.Dispose();
            await Manager.DisposeAsync();
        }
    }

    private static string TempLinkFile()
        => Path.Combine(Path.GetTempPath(), $"agnes-channel-links-{Guid.NewGuid():n}.json");

    private static Harness NewHarness(params IChannelBridge[] bridges)
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        var bus = new EventBus();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);

        var registry = new PluginRegistry<IChannelBridge>(bridges, b => b.Id);
        var links = new ChannelLinkStore(TempLinkFile(), TimeProvider.System);
        var prompts = new ChannelPromptTracker();
        var notifier = new ChannelBridgeNotifier(bus, registry, links, prompts);
        var router = new ChannelBridgeRouter(registry, links, prompts, manager);

        return new Harness
        {
            Manager = manager,
            Adapter = adapter,
            Store = store,
            Links = links,
            Notifier = notifier,
            Router = router,
        };
    }

    private static PermissionRequestedEvent Requested(string requestId, string title)
        => new(requestId, "tool-1", title,
            [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
             new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce)]);

    // Drives the scripted session to emit events for a prompt and waits until they're all persisted — by which
    // point the spine (and so the outbound notifier) has already observed each one.
    private static async Task EmitAsync(Harness h, string sessionId, params SessionEvent[] events)
    {
        h.Adapter.Session.OnPrompt = (_, s) =>
        {
            foreach (var e in events)
            {
                s.Emit(e);
            }

            return Task.FromResult(StopReason.EndTurn);
        };

        var before = await h.Store.GetHeadAsync(sessionId);
        await h.Manager.PromptAsync(sessionId, [new TextContent("go")]);
        var expected = before + 1 + events.Length;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await h.Store.GetHeadAsync(sessionId) < expected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task Permission_request_while_linked_sends_to_the_bridge()
    {
        var fake = new FakeChannelBridge();
        await using var h = NewHarness(fake);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Links.Link(fake.Id, "chat-1", "device-1");

        await EmitAsync(h, info.SessionId, Requested("req-1", "Delete build/"));

        var sent = Assert.Single(fake.SentMessages);
        Assert.Equal("chat-1", sent.ExternalChatId);
        Assert.Contains("Delete build/", sent.Message);
        Assert.Equal(info.SessionId, sent.Context.SessionId);
        Assert.Equal("req-1", sent.Context.RequestId);
        Assert.Equal(ChannelBridgeEventKind.PermissionRequest, sent.Context.Kind);
    }

    [Fact]
    public async Task Inbound_allow_from_linked_chat_answers_the_permission()
    {
        var fake = new FakeChannelBridge();
        await using var h = NewHarness(fake);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Links.Link(fake.Id, "chat-1", "device-1");

        await EmitAsync(h, info.SessionId, Requested("req-1", "Run tests"));
        await fake.RaiseInboundAsync("chat-1", "allow");

        // Resolved via the exact same path a paired client uses — the live session got the allow option id.
        Assert.Equal("allow", h.Adapter.Session.LastPermissionOptionId);
    }

    [Fact]
    public async Task Inbound_from_unlinked_chat_is_not_authorized()
    {
        var fake = new FakeChannelBridge();
        await using var h = NewHarness(fake);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        // No Link() call — chat-99 is anonymous.

        await EmitAsync(h, info.SessionId, Requested("req-1", "Force-push main"));

        // The unlinked chat was never notified (no link to fan out to)...
        Assert.Empty(fake.SentMessages);

        // ...and even a well-formed "allow" from it cannot approve or steer.
        await fake.RaiseInboundAsync("chat-99", "allow");
        Assert.Null(h.Adapter.Session.LastPermissionOptionId);

        var approvals = await h.Manager.GetOpenApprovalsAsync();
        Assert.Contains(approvals, a => a.RequestId == "req-1"); // stays open
    }

    [Fact]
    public async Task Unlink_invalidates_a_stale_link_and_stops_outbound()
    {
        var fake = new FakeChannelBridge();
        await using var h = NewHarness(fake);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Links.Link(fake.Id, "chat-1", "device-1");

        await EmitAsync(h, info.SessionId, Requested("req-1", "First"));
        Assert.Single(fake.SentMessages); // delivered while linked

        Assert.True(h.Links.Unlink(fake.Id, "chat-1"));

        // A new request fires no outbound to the now-unlinked chat.
        await EmitAsync(h, info.SessionId, Requested("req-2", "Second"));
        Assert.Single(fake.SentMessages); // still just the one from before the unlink

        // A late "allow" on the previously-linked chat cannot approve anything.
        await fake.RaiseInboundAsync("chat-1", "allow");
        Assert.Null(h.Adapter.Session.LastPermissionOptionId);
    }

    [Fact]
    public async Task Second_bridge_registered_through_the_registry_gets_the_same_trigger()
    {
        var fakeA = new FakeChannelBridge("bridge-a");
        var fakeB = new FakeChannelBridge("bridge-b");
        // Both bridges added purely through the registry — the notifier's code is unchanged.
        await using var h = NewHarness(fakeA, fakeB);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Links.Link(fakeA.Id, "chat-a", "device-1");
        h.Links.Link(fakeB.Id, "chat-b", "device-1");

        await EmitAsync(h, info.SessionId, Requested("req-1", "Deploy"));

        Assert.Equal("chat-a", Assert.Single(fakeA.SentMessages).ExternalChatId);
        Assert.Equal("chat-b", Assert.Single(fakeB.SentMessages).ExternalChatId);
    }
}
