using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Hosting;
using Agnes.Host.Notifications;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Push-notification plugin point (notifications/01): the host-driven dispatcher that fans a minimized
/// payload off the event spine to eligible registered devices (honoring master + per-trigger toggles and
/// active-session suppression), and the untrusted-host action guard that only auto-approves through the real
/// permission path when the acting device holds a currently-valid token. Everything offline: a
/// <see cref="FakeNotificationChannel"/> stands in for FCM/APNs and a <see cref="ScriptedAgentAdapter"/> drives
/// the session — no network.
/// </summary>
public sealed class PushNotificationTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    /// <summary>The host-side push wiring sharing one event bus with the SessionManager.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required SessionManager Manager { get; init; }
        public required ScriptedAgentAdapter Adapter { get; init; }
        public required IEventStore Store { get; init; }
        public required IEventBus Bus { get; init; }
        public required PushRegistrationStore Registrations { get; init; }
        public required ActiveSessionViewTracker Views { get; init; }
        public required PushNotificationDispatcher Dispatcher { get; init; }

        public async ValueTask DisposeAsync()
        {
            Dispatcher.Dispose();
            Registrations.Dispose();
            await Manager.DisposeAsync();
        }
    }

    private static string TempFile()
        => Path.Combine(Path.GetTempPath(), $"agnes-push-registrations-{Guid.NewGuid():n}.json");

    private static Harness NewHarness(params INotificationChannel[] channels)
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        var bus = new EventBus();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);

        var registry = new PluginRegistry<INotificationChannel>(channels, c => c.Id);
        var registrations = new PushRegistrationStore(TempFile(), bus);
        var views = new ActiveSessionViewTracker();
        var dispatcher = new PushNotificationDispatcher(bus, registry, registrations, views);

        return new Harness
        {
            Manager = manager,
            Adapter = adapter,
            Store = store,
            Bus = bus,
            Registrations = registrations,
            Views = views,
            Dispatcher = dispatcher,
        };
    }

    private static PermissionRequestedEvent Requested(string requestId, string title)
        => new(requestId, "tool-1", title,
            [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
             new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce)]);

    // Drives the scripted session to emit events for a prompt and waits until they're all persisted — by which
    // point the spine (and so the dispatcher) has already observed each one.
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
    public async Task Permission_request_sends_to_a_registered_device_with_the_right_trigger()
    {
        var channel = new FakeNotificationChannel();
        await using var h = NewHarness(channel);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Registrations.Register("device-1", channel.Id, "fcm-token-1");

        await EmitAsync(h, info.SessionId, Requested("req-1", "Delete build/"));

        var payload = Assert.Single(channel.Sent);
        Assert.Equal("device-1", payload.DeviceId);
        Assert.Equal(NotificationTrigger.PermissionRequest, payload.Trigger);
        Assert.Equal(info.SessionId, payload.SessionId);
        Assert.Contains("Delete build/", payload.ShortHint);
    }

    [Fact]
    public async Task Trigger_toggled_off_sends_nothing()
    {
        var channel = new FakeNotificationChannel();
        await using var h = NewHarness(channel);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Registrations.Register("device-1", channel.Id, "fcm-token-1");
        // Permission trigger off, but turn-ready still on.
        h.Registrations.SetPreferences("device-1", enabled: true, new PushTriggerPrefs(PermissionRequest: false));

        await EmitAsync(h, info.SessionId, Requested("req-1", "Delete build/"));

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Master_off_sends_nothing_even_with_the_trigger_on()
    {
        var channel = new FakeNotificationChannel();
        await using var h = NewHarness(channel);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Registrations.Register("device-1", channel.Id, "fcm-token-1");
        h.Registrations.SetPreferences("device-1", enabled: false, new PushTriggerPrefs(PermissionRequest: true));

        await EmitAsync(h, info.SessionId, Requested("req-1", "Delete build/"));

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Active_viewer_is_suppressed_on_that_device_only()
    {
        var channel = new FakeNotificationChannel();
        await using var h = NewHarness(channel);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Registrations.Register("device-A", channel.Id, "token-A");
        h.Registrations.Register("device-B", channel.Id, "token-B");

        // device-A is actively viewing this exact session; device-B is not.
        h.Views.MarkViewing("device-A", info.SessionId);

        await EmitAsync(h, info.SessionId, new TurnEndedEvent(StopReason.EndTurn));

        var payload = Assert.Single(channel.Sent);
        Assert.Equal("device-B", payload.DeviceId);
        Assert.Equal(NotificationTrigger.TurnReady, payload.Trigger);
    }

    [Fact]
    public async Task Second_channel_registered_through_the_registry_also_receives_the_trigger()
    {
        var mobile = new FakeNotificationChannel("mobile-push");
        var other = new FakeNotificationChannel("other-push");
        // Both channels added purely through the registry — the dispatcher's code is unchanged.
        await using var h = NewHarness(mobile, other);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Registrations.Register("device-1", mobile.Id, "token-1");
        h.Registrations.Register("device-2", other.Id, "token-2");

        await EmitAsync(h, info.SessionId, new TurnEndedEvent(StopReason.EndTurn));

        Assert.Equal("device-1", Assert.Single(mobile.Sent).DeviceId);
        Assert.Equal("device-2", Assert.Single(other.Sent).DeviceId);
    }

    [Fact]
    public async Task Revoking_a_device_pairing_removes_its_push_registration()
    {
        var channel = new FakeNotificationChannel();
        await using var h = NewHarness(channel);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        h.Registrations.Register("device-1", channel.Id, "token-1");

        // The store observes DeviceRevokedEvent off the spine — the same event device management dispatches.
        await h.Bus.DispatchAsync(new DeviceRevokedEvent("device-1"));
        Assert.Null(h.Registrations.Get("device-1"));

        await EmitAsync(h, info.SessionId, Requested("req-1", "After revoke"));

        Assert.Empty(channel.Sent);
    }

    // ---- interactive-action safety guard ----

    private sealed class GuardHarness : IAsyncDisposable
    {
        public required SessionManager Manager { get; init; }
        public required ScriptedAgentAdapter Adapter { get; init; }
        public required IEventStore Store { get; init; }
        public required DeviceRegistry Devices { get; init; }
        public required PushActionRouter Router { get; init; }
        public required string DeviceFile { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            if (File.Exists(DeviceFile))
            {
                File.Delete(DeviceFile);
            }
        }
    }

    private static GuardHarness NewGuardHarness()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance);
        var deviceFile = Path.Combine(Path.GetTempPath(), $"agnes-devices-{Guid.NewGuid():n}.json");
        var devices = new DeviceRegistry(bootstrapToken: null, deviceFile);
        var router = new PushActionRouter(devices, manager);
        return new GuardHarness
        {
            Manager = manager,
            Adapter = adapter,
            Store = store,
            Devices = devices,
            Router = router,
            DeviceFile = deviceFile,
        };
    }

    // Emit a pending permission request into a live session (no spine dependency needed for the guard).
    private static async Task EmitPermissionAsync(GuardHarness h, string sessionId, string requestId)
    {
        h.Adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(Requested(requestId, "Run tests"));
            return Task.FromResult(StopReason.EndTurn);
        };

        var before = await h.Store.GetHeadAsync(sessionId);
        await h.Manager.PromptAsync(sessionId, [new TextContent("go")]);
        var expected = before + 2; // the prompt event + the permission event
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await h.Store.GetHeadAsync(sessionId) < expected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task Valid_token_routes_the_approval_through_the_real_permission_path()
    {
        await using var h = NewGuardHarness();
        var paired = h.Devices.TryPair(h.Devices.PairingCode, "phone");
        Assert.NotNull(paired);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await EmitPermissionAsync(h, info.SessionId, "req-1");

        var outcome = await h.Router.RespondToPermissionAsync(
            paired!.DeviceId, paired.Token, info.SessionId, "req-1", "allow");

        Assert.Equal(PushActionOutcome.Approved, outcome);
        // Routed through the same path a paired client uses — the live session got the allow option id.
        Assert.Equal("allow", h.Adapter.Session.LastPermissionOptionId);
    }

    [Fact]
    public async Task Stale_or_wrong_token_is_rejected_and_never_approves()
    {
        await using var h = NewGuardHarness();
        var paired = h.Devices.TryPair(h.Devices.PairingCode, "phone");
        Assert.NotNull(paired);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await EmitPermissionAsync(h, info.SessionId, "req-1");

        // A forged/stale token that isn't this device's valid token.
        var outcome = await h.Router.RespondToPermissionAsync(
            paired!.DeviceId, "not-a-real-token", info.SessionId, "req-1", "allow");

        Assert.Equal(PushActionOutcome.OpenAppRequired, outcome);
        Assert.Null(h.Adapter.Session.LastPermissionOptionId); // stays pending; nothing approved
    }

    [Fact]
    public async Task Revoked_pairing_falls_back_to_open_app_instead_of_approving()
    {
        await using var h = NewGuardHarness();
        var paired = h.Devices.TryPair(h.Devices.PairingCode, "phone");
        Assert.NotNull(paired);
        var info = await h.Manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await EmitPermissionAsync(h, info.SessionId, "req-1");

        // Pairing revoked through existing device management — the token no longer resolves.
        Assert.True(h.Devices.Revoke(paired!.DeviceId));

        var outcome = await h.Router.RespondToPermissionAsync(
            paired.DeviceId, paired.Token, info.SessionId, "req-1", "allow");

        Assert.Equal(PushActionOutcome.OpenAppRequired, outcome);
        Assert.Null(h.Adapter.Session.LastPermissionOptionId);
    }

    [Fact]
    public async Task Unknown_session_is_rejected_and_never_approves()
    {
        await using var h = NewGuardHarness();
        var paired = h.Devices.TryPair(h.Devices.PairingCode, "phone");
        Assert.NotNull(paired);

        var outcome = await h.Router.RespondToPermissionAsync(
            paired!.DeviceId, paired.Token, "no-such-session", "req-1", "allow");

        Assert.Equal(PushActionOutcome.OpenAppRequired, outcome);
        Assert.Null(h.Adapter.Session.LastPermissionOptionId);
    }
}
