using System.Net;
using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The first REAL quota reporter (.ideas/providers/03): <see cref="ClaudeQuotaProvider"/> probing Anthropic's
/// OAuth usage endpoint. Everything here is offline — a stub <see cref="HttpMessageHandler"/> returns canned
/// bodies, the OAuth token is injected, and a hand-advanced clock makes staleness deterministic. No network.
/// </summary>
public class ClaudeQuotaProviderTests
{
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A canned HTTP responder keyed by call index; counts how many probes reached the wire.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, (HttpStatusCode Status, string Body)> _responder;
        public StubHandler(Func<int, (HttpStatusCode Status, string Body)> responder) => _responder = responder;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _responder(Calls);
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    // No retries in tests: keeps probes deterministic and free of real backoff delays.
    private static readonly ClaudeQuotaResilienceOptions NoRetry = new() { MaxRetries = 0 };

    // The live flat-bucket shape, redacted, as captured from the real endpoint (mirrors the CodeyBox fixture).
    private const string FlatUsageJson = """
        {
          "plan_type": "max",
          "five_hour": { "utilization": 3.0, "resets_at": "2026-05-10T00:19:59.94+00:00" },
          "seven_day": { "utilization": 84.0, "resets_at": "2026-05-10T22:59:59.94+00:00" },
          "seven_day_opus": null,
          "seven_day_sonnet": { "utilization": 100.0, "resets_at": "2026-05-10T23:00:00.94+00:00" }
        }
        """;

    private static ClaudeQuotaProvider Provider(
        StubHandler handler, TimeProvider time, string? token = "oauth-access-token")
        => new(new HttpClient(handler), _ => Task.FromResult(token), time, NoRetry);

    [Fact]
    public async Task Canned_success_maps_to_a_snapshot_with_plan_label_meters_and_fetched_at()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var handler = new StubHandler(_ => (HttpStatusCode.OK, FlatUsageJson));
        var provider = Provider(handler, clock);

        var snapshot = await provider.GetQuotaAsync("claude-personal");

        Assert.NotNull(snapshot);
        Assert.Equal("Claude Max", snapshot!.PlanLabel);
        Assert.Equal(clock.GetUtcNow(), snapshot.FetchedAt);

        // Three windows reported a figure; the null opus bucket contributes nothing.
        Assert.Collection(snapshot.Meters,
            m => AssertMeter(m, "5-hour limit", 3),
            m => AssertMeter(m, "7-day limit", 84),
            m => AssertMeter(m, "7-day limit (Sonnet)", 100));
    }

    private static void AssertMeter(QuotaMeter meter, string name, double used)
    {
        Assert.Equal(name, meter.Name);
        Assert.Equal(used, meter.Used);
        Assert.Equal(100, meter.Limit);
        Assert.Equal("%", meter.Unit);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_transient_failure_after_a_prior_success_retains_the_last_good_stale_snapshot(HttpStatusCode transient)
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        // First probe succeeds; every later probe fails transiently.
        var handler = new StubHandler(call => call == 0
            ? (HttpStatusCode.OK, FlatUsageJson)
            : (transient, "upstream unavailable"));
        var provider = Provider(handler, clock);

        var fresh = await provider.GetQuotaAsync("claude-personal");
        Assert.NotNull(fresh);
        var capturedAt = fresh!.FetchedAt;

        clock.Advance(TimeSpan.FromMinutes(2)); // still well inside MaxStaleness
        var stale = await provider.GetQuotaAsync("claude-personal");

        Assert.NotNull(stale); // retained, NOT null
        Assert.Equal("Claude Max", stale!.PlanLabel);
        // Its FetchedAt is the ORIGINAL retrieval time — the honest staleness indicator.
        Assert.Equal(capturedAt, stale.FetchedAt);
    }

    [Fact]
    public async Task A_persistent_failure_with_no_prior_success_reports_null_without_throwing()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var handler = new StubHandler(_ => (HttpStatusCode.InternalServerError, "down"));
        var provider = Provider(handler, clock);

        Assert.Null(await provider.GetQuotaAsync("claude-personal"));
    }

    [Fact]
    public async Task A_permanent_failure_drops_a_prior_reading_and_reports_null()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        // A non-retryable 401 (revoked/expired token) after a success: the prior reading is no longer trusted.
        var handler = new StubHandler(call => call == 0
            ? (HttpStatusCode.OK, FlatUsageJson)
            : (HttpStatusCode.Unauthorized, "revoked"));
        var provider = Provider(handler, clock);

        Assert.NotNull(await provider.GetQuotaAsync("claude-personal"));
        Assert.Null(await provider.GetQuotaAsync("claude-personal"));
    }

    [Fact]
    public async Task No_token_reports_null_without_making_a_request()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var handler = new StubHandler(_ => (HttpStatusCode.OK, FlatUsageJson));
        var provider = Provider(handler, clock, token: null);

        Assert.Null(await provider.GetQuotaAsync("claude-personal"));
        Assert.Equal(0, handler.Calls); // never hit the wire with no credential
    }

    [Fact]
    public async Task Parsing_tolerates_a_missing_or_extra_field()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        // Only one window present, no plan_type, and an unknown extra key the deserialiser must ignore.
        const string sparse = """
            {
              "five_hour": { "utilization": 12.5 },
              "some_future_field": { "nested": [1, 2, 3] }
            }
            """;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, sparse));
        var provider = Provider(handler, clock);

        var snapshot = await provider.GetQuotaAsync("claude-personal");

        Assert.NotNull(snapshot);
        Assert.Equal("Claude", snapshot!.PlanLabel); // no plan_type -> generic label
        var meter = Assert.Single(snapshot.Meters);
        AssertMeter(meter, "5-hour limit", 12.5);
    }

    [Fact]
    public async Task An_unrecognised_body_reports_null()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        // Valid JSON but carrying no window we recognise -> unusable reading (null), not a crash.
        var handler = new StubHandler(_ => (HttpStatusCode.OK, """{ "hello": "world" }"""));
        var provider = Provider(handler, clock);

        Assert.Null(await provider.GetQuotaAsync("claude-personal"));
    }

    [Fact]
    public async Task Registered_via_the_registry_QuotaService_returns_and_caches_the_claude_snapshot()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var handler = new StubHandler(_ => (HttpStatusCode.OK, FlatUsageJson));
        var provider = Provider(handler, clock);

        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, ClaudeQuotaProvider.ProviderId, "Claude", "personal"));
        var registry = new PluginRegistry<IConnectedServiceProvider>([provider], p => p.Id);
        var service = new QuotaService(store, registry, clock, TimeSpan.FromMinutes(5));

        var first = await service.GetQuotaAsync(profile.Id);
        clock.Advance(TimeSpan.FromMinutes(2)); // inside the QuotaService staleness window
        var second = await service.GetQuotaAsync(profile.Id);

        Assert.NotNull(first);
        Assert.Equal("Claude Max", first!.PlanLabel);
        Assert.NotNull(second);
        Assert.Equal(1, handler.Calls); // second read served from the QuotaService cache; no redundant probe
    }

    [Fact]
    public async Task Token_source_reads_the_access_token_from_the_credentials_file()
    {
        // Exercises the reuse of ClaudeCredentialProvider.TrySanitise via ClaudeOAuthTokenSource against a
        // canned credentials file in a temp home dir (PH2080: no absolute-path literals).
        var home = Path.Combine(Path.GetTempPath(), "agnes-claude-quota-" + Guid.NewGuid().ToString("n"));
        var claudeDir = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claudeDir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(claudeDir, ".credentials.json"),
                """{ "claudeAiOauth": { "accessToken": "sk-ant-oauth-abc", "refreshToken": "sk-ant-refresh-SECRET", "expiresAt": 1778091218 } }""");

            var source = new ClaudeOAuthTokenSource(home);
            Assert.Equal("sk-ant-oauth-abc", await source.ReadAccessTokenAsync());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task Token_source_returns_null_when_no_credentials_file_exists()
    {
        var home = Path.Combine(Path.GetTempPath(), "agnes-claude-quota-missing-" + Guid.NewGuid().ToString("n"));
        var source = new ClaudeOAuthTokenSource(home);
        Assert.Null(await source.ReadAccessTokenAsync());
    }
}
