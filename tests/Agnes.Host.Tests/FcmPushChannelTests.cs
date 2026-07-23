using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Hosting;
using Agnes.Host.Notifications;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The REAL FCM channel (notifications/01): its payload→message mapping and config-gating, exercised entirely
/// offline through the <see cref="IFcmSender"/> seam with a fake — no real Firebase. The actual
/// <see cref="FirebaseFcmSender"/> is deliberately NOT unit-tested here: it needs a live service-account
/// credential and is exercised only against a configured Firebase project. The channel also fans out correctly
/// through the existing <see cref="PushNotificationDispatcher"/> with no dispatcher change.
/// </summary>
public sealed class FcmPushChannelTests
{
    /// <summary>An in-memory <see cref="IFcmSender"/> capturing every send — stands in for FirebaseAdmin.</summary>
    private sealed class FakeFcmSender : IFcmSender
    {
        public sealed record Sent(
            string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data);

        private readonly ConcurrentQueue<Sent> _sent = new();
        private readonly bool _throw;

        public FakeFcmSender(bool @throw = false) => _throw = @throw;

        public IReadOnlyList<Sent> Messages => _sent.ToArray();

        public Task SendAsync(
            string registrationToken, string title, string body,
            IReadOnlyDictionary<string, string> data, CancellationToken ct)
        {
            if (_throw)
            {
                throw new InvalidOperationException("simulated FCM transport failure");
            }

            _sent.Enqueue(new Sent(registrationToken, title, body, data));
            return Task.CompletedTask;
        }
    }

    private static NotificationPayload Payload(
        string device = "device-1",
        NotificationTrigger trigger = NotificationTrigger.PermissionRequest,
        string hint = "Permission: Delete build/",
        string session = "session-1")
        => new(device, trigger, hint, session);

    [Fact]
    public async Task Registers_token_then_maps_payload_to_a_message_with_token_title_body_and_data()
    {
        var sender = new FakeFcmSender();
        var channel = new FcmPushChannel(sender);

        await channel.RegisterAsync("device-1", "fcm-token-1");
        Assert.Equal("fcm-token-1", channel.TokenFor("device-1"));

        await channel.SendAsync(Payload(session: "session-42", hint: "Permission: Delete build/"));

        var msg = Assert.Single(sender.Messages);
        Assert.Equal("fcm-token-1", msg.Token);
        Assert.Equal("Permission needed", msg.Title);
        Assert.Equal("Permission: Delete build/", msg.Body);
        // Routing data the app deep-links on: trigger + session id (+ device id) present.
        Assert.Equal("session-42", msg.Data["sessionId"]);
        Assert.Equal("device-1", msg.Data["deviceId"]);
        Assert.Equal(nameof(NotificationTrigger.PermissionRequest), msg.Data["trigger"]);
    }

    [Fact]
    public async Task Title_tracks_the_trigger()
    {
        var sender = new FakeFcmSender();
        var channel = new FcmPushChannel(sender);
        await channel.RegisterAsync("device-1", "tok");

        await channel.SendAsync(Payload(trigger: NotificationTrigger.TurnReady, hint: "Turn finished"));
        await channel.SendAsync(Payload(trigger: NotificationTrigger.UserActionRequest, hint: "1 question"));

        Assert.Equal("Agent ready", sender.Messages[0].Title);
        Assert.Equal("Action needed", sender.Messages[1].Title);
    }

    [Fact]
    public async Task No_sender_means_not_usable_and_send_is_a_silent_no_op()
    {
        // Null sender models "no Agnes:Push:Fcm:ServiceAccountJson configured".
        var channel = new FcmPushChannel(sender: null);
        Assert.False(channel.IsUsable);

        await channel.RegisterAsync("device-1", "tok");
        // Must not throw even though there is no transport behind it.
        await channel.SendAsync(Payload());
        // Nothing observable happened — no exception, no delivery.
    }

    [Fact]
    public async Task Configured_sender_reports_usable()
    {
        var channel = new FcmPushChannel(new FakeFcmSender());
        Assert.True(channel.IsUsable);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Missing_device_token_is_skipped_without_sending()
    {
        var sender = new FakeFcmSender();
        var channel = new FcmPushChannel(sender);
        // No RegisterAsync for this device → no token on file.
        await channel.SendAsync(Payload(device: "unregistered"));
        Assert.Empty(sender.Messages);
    }

    [Fact]
    public async Task A_sender_failure_is_swallowed_so_delivery_stays_independent()
    {
        var sender = new FakeFcmSender(@throw: true);
        var channel = new FcmPushChannel(sender);
        await channel.RegisterAsync("device-1", "tok");
        // The FCM transport throws, but SendAsync must not propagate it.
        await channel.SendAsync(Payload());
    }

    // ---- fans out through the existing dispatcher, no dispatcher change ----

    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static string TempFile()
        => Path.Combine(Path.GetTempPath(), $"agnes-push-fcm-{Guid.NewGuid():n}.json");

    [Fact]
    public async Task Dispatcher_fans_a_trigger_to_the_registered_fcm_channel()
    {
        var sender = new FakeFcmSender();
        var channel = new FcmPushChannel(sender);

        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        var bus = new EventBus();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);
        var registry = new PluginRegistry<INotificationChannel>([channel], c => c.Id);
        var registrations = new PushRegistrationStore(TempFile(), bus);
        var views = new ActiveSessionViewTracker();
        using var dispatcher = new PushNotificationDispatcher(bus, registry, registrations, views);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        // The device registers against the "fcm" channel; the hub calls both the store and channel.RegisterAsync.
        registrations.Register("device-1", channel.Id, "fcm-token-1");
        await channel.RegisterAsync("device-1", "fcm-token-1");

        adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        var before = await store.GetHeadAsync(info.SessionId);
        await manager.PromptAsync(info.SessionId, [new TextContent("go")]);
        var expected = before + 2; // prompt event + turn-ended event
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await store.GetHeadAsync(info.SessionId) < expected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }

        var msg = Assert.Single(sender.Messages);
        Assert.Equal("fcm-token-1", msg.Token);
        Assert.Equal("Agent ready", msg.Title);
        Assert.Equal(info.SessionId, msg.Data["sessionId"]);

        registrations.Dispose();
        await manager.DisposeAsync();
    }
}
