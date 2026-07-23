using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The connected-service quota surface (.ideas/providers/03): the OPTIONAL <see cref="IQuotaReportingProvider"/>
/// capability plus the host's caching <see cref="QuotaService"/>. Everything here is offline — no network, and
/// a hand-advanced clock makes the staleness window deterministic.
/// </summary>
public class QuotaServiceTests
{
    // A clock we advance by hand so the caching window is deterministic (no real waiting).
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A provider that CAN report usage, counting how often the (would-be network) call is made.</summary>
    private sealed class ReportingProvider : IConnectedServiceProvider, IQuotaReportingProvider
    {
        private readonly QuotaSnapshot _snapshot;
        public ReportingProvider(string id, QuotaSnapshot snapshot) { Id = id; _snapshot = snapshot; }

        public string Id { get; }
        public string DisplayName => Id;
        public int Calls { get; private set; }

        public Task<ResolvedServiceCredential> ResolveAsync(ConnectedServiceProfile profile, CancellationToken ct = default)
            => Task.FromResult(new ResolvedServiceCredential("secret", ExpiresAt: null));

        public Task<QuotaSnapshot?> GetQuotaAsync(string profileId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<QuotaSnapshot?>(_snapshot);
        }
    }

    /// <summary>A provider that does NOT implement the quota capability (the common case).</summary>
    private sealed class PlainProvider : IConnectedServiceProvider
    {
        public string Id => "plain";
        public string DisplayName => "Plain";
        public Task<ResolvedServiceCredential> ResolveAsync(ConnectedServiceProfile profile, CancellationToken ct = default)
            => Task.FromResult(new ResolvedServiceCredential("secret", ExpiresAt: null));
    }

    private static QuotaService ServiceOver(
        ConnectedServiceProfileStore store,
        TimeProvider time,
        TimeSpan staleness,
        params IConnectedServiceProvider[] providers)
    {
        var registry = new PluginRegistry<IConnectedServiceProvider>(providers, p => p.Id);
        return new QuotaService(store, registry, time, staleness);
    }

    private static QuotaSnapshot SampleSnapshot(DateTimeOffset at) => new(
        "Team plan",
        [new QuotaMeter("Monthly messages", Used: 40, Limit: 200, Unit: "requests")],
        at);

    [Fact]
    public async Task A_provider_that_reports_quota_yields_its_snapshot()
    {
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "reporting", "Reporting", "personal"));
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var provider = new ReportingProvider("reporting", SampleSnapshot(clock.GetUtcNow()));
        var service = ServiceOver(store, clock, TimeSpan.FromMinutes(5), provider);

        var snapshot = await service.GetQuotaAsync(profile.Id);

        Assert.NotNull(snapshot);
        Assert.Equal("Team plan", snapshot!.PlanLabel);
        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal("Monthly messages", meter.Name);
        Assert.Equal(40, meter.Used);
        Assert.Equal(200, meter.Limit);
    }

    [Fact]
    public async Task A_provider_without_the_capability_reports_no_quota()
    {
        // The plain provider doesn't implement IQuotaReportingProvider — its absence is a clean null
        // ("not supported"), NOT an error.
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "plain", "Plain", "personal"));
        var service = ServiceOver(store, TimeProvider.System, TimeSpan.FromMinutes(5), new PlainProvider());

        Assert.Null(await service.GetQuotaAsync(profile.Id));
    }

    [Fact]
    public async Task Repeated_requests_within_the_window_hit_the_provider_once()
    {
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "reporting", "Reporting", "personal"));
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var provider = new ReportingProvider("reporting", SampleSnapshot(clock.GetUtcNow()));
        var service = ServiceOver(store, clock, TimeSpan.FromMinutes(5), provider);

        await service.GetQuotaAsync(profile.Id);
        clock.Advance(TimeSpan.FromMinutes(2)); // still inside the 5-minute window
        await service.GetQuotaAsync(profile.Id);
        clock.Advance(TimeSpan.FromMinutes(2)); // total 4 minutes — still inside
        await service.GetQuotaAsync(profile.Id);

        Assert.Equal(1, provider.Calls); // served from cache; no redundant provider call
    }

    [Fact]
    public async Task After_the_window_expires_it_refetches()
    {
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "reporting", "Reporting", "personal"));
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var provider = new ReportingProvider("reporting", SampleSnapshot(clock.GetUtcNow()));
        var service = ServiceOver(store, clock, TimeSpan.FromMinutes(5), provider);

        await service.GetQuotaAsync(profile.Id);
        clock.Advance(TimeSpan.FromMinutes(6)); // past the window
        await service.GetQuotaAsync(profile.Id);

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task An_unknown_profile_reports_no_quota_without_throwing()
    {
        var store = new ConnectedServiceProfileStore();
        var service = ServiceOver(store, TimeProvider.System, TimeSpan.FromMinutes(5), new PlainProvider());

        Assert.Null(await service.GetQuotaAsync("does-not-exist"));
    }

    [Fact]
    public async Task The_template_provider_reports_a_stub_snapshot_with_meters()
    {
        // The built-in template provider implements the capability with a placeholder plan — the seam a real
        // provider replaces with a usage-endpoint call.
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "personal"));
        var service = ServiceOver(store, TimeProvider.System, TimeSpan.FromMinutes(5),
            new TemplateConnectedServiceProvider());

        var snapshot = await service.GetQuotaAsync(profile.Id);

        Assert.NotNull(snapshot);
        Assert.Equal("Template plan", snapshot!.PlanLabel);
        Assert.NotEmpty(snapshot.Meters);
    }

    [Fact]
    public async Task A_provider_fetch_failure_surfaces_as_unavailable_not_a_crash()
    {
        var store = new ConnectedServiceProfileStore();
        var profile = store.Save(new ConnectedServiceProfile(string.Empty, "template", "Template", "personal"));
        // A quota lookup that throws stands in for a provider API error / network failure.
        var provider = new TemplateConnectedServiceProvider(
            quotaLookup: _ => throw new InvalidOperationException("usage endpoint down"));
        var service = ServiceOver(store, TimeProvider.System, TimeSpan.FromMinutes(5), provider);

        // No exception escapes — the caller sees a clean null ("unavailable").
        Assert.Null(await service.GetQuotaAsync(profile.Id));
    }

    [Fact]
    public void A_snapshot_serializes_its_plan_and_meters_and_carries_no_secret()
    {
        var snapshot = new QuotaSnapshot(
            "Pro",
            [new QuotaMeter("Input tokens", Used: 12_000, Limit: 1_000_000, Unit: "tokens")],
            DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(snapshot);

        Assert.Contains("Pro", json, StringComparison.Ordinal);
        Assert.Contains("Input tokens", json, StringComparison.Ordinal);
        Assert.Contains("1000000", json.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        // Round-trips back to the same typed values — the wire shape is the domain shape (list members
        // compare by reference on the record, so assert the fields, not whole-record equality).
        var roundTripped = JsonSerializer.Deserialize<QuotaSnapshot>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(snapshot.PlanLabel, roundTripped!.PlanLabel);
        Assert.Equal(snapshot.FetchedAt, roundTripped.FetchedAt);
        Assert.Equal(snapshot.Meters, roundTripped.Meters); // element-wise record equality
    }
}
