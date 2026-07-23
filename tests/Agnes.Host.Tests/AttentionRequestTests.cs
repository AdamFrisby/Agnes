using System.Net;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Attention;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The generic human-in-the-loop attention API (extensibility/06): create over REST, surface in the SAME
/// approvals inbox as session permissions, answer → record → callback POST (with bounded retry) or poll,
/// timeouts, and per-caller scoping. Fully offline — the callback HTTP goes through a stub handler and time
/// is a controllable clock, so there are no real network calls or delays.
/// </summary>
public class AttentionRequestTests
{
    // A clock we advance by hand so timeout sweeps are deterministic (no real waiting).
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // Records every callback POST (deserialized with the snake_case wire options the poster uses) and returns
    // a fixed status — OK to confirm delivery, or a failure to exercise retry/backoff.
    private sealed class StubCallbackHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private readonly HttpStatusCode _status;
        public StubCallbackHandler(HttpStatusCode status = HttpStatusCode.OK) => _status = status;

        public List<AttentionCallbackPayload> Received { get; } = new();
        public List<string> Urls { get; } = new();
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Urls.Add(request.RequestUri!.ToString());
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<AttentionCallbackPayload>(body, Wire);
            if (payload is not null)
            {
                Received.Add(payload);
            }

            return new HttpResponseMessage(_status);
        }
    }

    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static AttentionCallbackPoster NoDelayPoster(HttpMessageHandler handler, int maxAttempts = 3)
        => new(new HttpClient(handler), maxAttempts,
            backoff: _ => TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (AttentionRequestStore Store, AttentionRequestService Service, MutableClock Clock) NewService(
        HttpMessageHandler handler, int maxAttempts = 3)
    {
        var clock = new MutableClock(Start);
        var store = new AttentionRequestStore(Path.Combine(Path.GetTempPath(), "agnes-attn-" + Guid.NewGuid().ToString("n"), "attention.json"), clock);
        var service = new AttentionRequestService(store, NoDelayPoster(handler, maxAttempts), clock);
        return (store, service, clock);
    }

    [Fact]
    public async Task Created_request_appears_in_open_approvals_pending_and_source_labeled()
    {
        var (store, service, _) = NewService(new StubCallbackHandler());
        var request = service.Create("caller-a", "my-ci", "Deploy to production?", ["approve", "reject"], callbackUrl: null, timeoutSeconds: null);

        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(),
            new NullBroadcaster(), NullLoggerFactory.Instance, attention: store);

        var approvals = await manager.GetOpenApprovalsAsync();

        var only = Assert.Single(approvals);
        Assert.Equal(OpenApprovalKind.ExternalAttention, only.Kind);
        Assert.Equal("my-ci", only.Source);
        Assert.Null(only.SessionId);
        Assert.Equal(request.Id, only.RequestId);
        Assert.Equal("Deploy to production?", only.Title);
        Assert.Equal(["approve", "reject"], only.Options);
    }

    [Fact]
    public async Task Answer_with_callback_records_answer_and_posts_it()
    {
        var handler = new StubCallbackHandler(HttpStatusCode.OK);
        var (store, service, _) = NewService(handler);
        var request = service.Create("caller-a", "my-ci", "Deploy?", ["approve", "reject"], callbackUrl: "https://ci.example/hook", timeoutSeconds: null);

        var outcome = service.Answer(request.Id, "approve");
        Assert.NotNull(outcome);
        Assert.True(await outcome!.CallbackDelivery);

        // Answer recorded on the request…
        var stored = store.Get(request.Id);
        Assert.Equal(AttentionStatus.Answered, stored!.Status);
        Assert.Equal("approve", stored.Answer);

        // …and delivered to the callback URL as a real answer.
        var posted = Assert.Single(handler.Received);
        Assert.Equal("https://ci.example/hook", Assert.Single(handler.Urls));
        Assert.Equal("answer", posted.Kind);
        Assert.Equal("approve", posted.Answer);
        Assert.Equal(request.Id, posted.RequestId);
        Assert.Equal("my-ci", posted.Source);
    }

    [Fact]
    public async Task Answer_without_callback_is_available_via_the_store_and_no_post_is_made()
    {
        var handler = new StubCallbackHandler();
        var (store, service, _) = NewService(handler);
        var request = service.Create("caller-a", "script", "Continue?", ["yes", "no"], callbackUrl: null, timeoutSeconds: null);

        var outcome = service.Answer(request.Id, "yes");
        Assert.NotNull(outcome);
        Assert.True(await outcome!.CallbackDelivery); // trivially true — nothing to deliver.

        Assert.Empty(handler.Received); // no callback attempted at all.

        // Pollable via the owner-scoped read.
        var polled = service.GetForOwner(request.Id, "caller-a");
        Assert.Equal(AttentionStatus.Answered, polled!.Status);
        Assert.Equal("yes", polled.Answer);
    }

    [Fact]
    public async Task Timeout_expires_the_request_rejects_later_answers_and_posts_a_distinct_timeout()
    {
        var handler = new StubCallbackHandler(HttpStatusCode.OK);
        var (store, service, clock) = NewService(handler);
        var request = service.Create("caller-a", "my-ci", "Deploy?", ["approve", "reject"], callbackUrl: "https://ci.example/hook", timeoutSeconds: 60);

        // Not yet due: sweeping does nothing.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(service.SweepExpired());
        Assert.Equal(AttentionStatus.Pending, store.Get(request.Id)!.Status);

        // Past the deadline: it expires and a distinct timeout callback is delivered.
        clock.Advance(TimeSpan.FromSeconds(31));
        var deliveries = service.SweepExpired();
        foreach (var d in deliveries)
        {
            Assert.True(await d);
        }

        Assert.Equal(AttentionStatus.Expired, store.Get(request.Id)!.Status);

        var posted = Assert.Single(handler.Received);
        Assert.Equal("timeout", posted.Kind);
        Assert.Null(posted.Answer);

        // No answer is accepted once expired.
        Assert.Null(service.Answer(request.Id, "approve"));
        Assert.Equal(AttentionStatus.Expired, store.Get(request.Id)!.Status);
        Assert.Null(store.Get(request.Id)!.Answer);
    }

    [Fact]
    public void Caller_scoping_a_request_is_only_readable_by_its_owner()
    {
        var (_, service, _) = NewService(new StubCallbackHandler());
        var request = service.Create("caller-a", "my-ci", "Deploy?", ["approve"], callbackUrl: null, timeoutSeconds: null);

        // Owner A can read it…
        Assert.NotNull(service.GetForOwner(request.Id, "caller-a"));
        // …caller B (a different token) cannot — indistinguishable from "not found".
        Assert.Null(service.GetForOwner(request.Id, "caller-b"));
    }

    [Fact]
    public async Task Failing_callback_is_retried_up_to_the_cap_then_stops_and_answer_stays_pollable()
    {
        var handler = new StubCallbackHandler(HttpStatusCode.InternalServerError); // always fails
        var (store, service, _) = NewService(handler, maxAttempts: 3);
        var request = service.Create("caller-a", "my-ci", "Deploy?", ["approve"], callbackUrl: "https://down.example/hook", timeoutSeconds: null);

        var outcome = service.Answer(request.Id, "approve");
        Assert.NotNull(outcome);
        Assert.False(await outcome!.CallbackDelivery); // gave up.

        // Bounded: exactly the attempt cap, not unbounded retries.
        Assert.Equal(3, handler.Calls);

        // The answer is still recorded and pollable despite the callback never landing.
        var stored = store.Get(request.Id);
        Assert.Equal(AttentionStatus.Answered, stored!.Status);
        Assert.Equal("approve", stored.Answer);
    }

    [Fact]
    public void Resolve_caller_id_scopes_tokens_to_their_owning_device()
    {
        var path = Path.Combine(Path.GetTempPath(), "agnes-devices-" + Guid.NewGuid().ToString("n"), "devices.json");
        var registry = new Agnes.Host.Hosting.DeviceRegistry(bootstrapToken: null, path, pairingEnabled: true);

        var a = registry.TryPair(registry.PairingCode, "caller-a");
        var b = registry.TryPair(registry.PairingCode, "caller-b");
        Assert.NotNull(a);
        Assert.NotNull(b);

        Assert.Equal(a!.DeviceId, registry.ResolveCallerId(a.Token));
        Assert.Equal(b!.DeviceId, registry.ResolveCallerId(b.Token));
        Assert.NotEqual(registry.ResolveCallerId(a.Token), registry.ResolveCallerId(b.Token));
        Assert.Null(registry.ResolveCallerId("not-a-real-token"));
        Assert.Null(registry.ResolveCallerId(null));
    }
}
