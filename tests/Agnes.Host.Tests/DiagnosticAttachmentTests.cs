using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Host.Ops;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Tests;

/// <summary>
/// The owner-only, opt-in diagnostic-attachment gating: the payload reaches the sink ONLY when the caller is
/// the authorized owner, the capability is enabled, and the user opted in for this report. Otherwise it stays
/// null exactly as on the default path — and the public browser-fallback URL never carries it.
/// </summary>
public class DiagnosticAttachmentTests : IDisposable
{
    private const string OwnerId = "owner-device";

    private readonly string _deviceFile = Path.Combine(Path.GetTempPath(), $"agnes-owner-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_deviceFile))
        {
            File.Delete(_deviceFile);
        }
    }

    private static BugReport Report()
        => new("Crash on prompt", "It exploded when I typed.", null, null, DiagnosticPayload: null);

    private static DiagnosticCollector Collector()
    {
        var log = new HostLogRingBuffer(50);
        log.Add(new HostLogLine(DateTimeOffset.UtcNow, LogLevel.Warning, "Agnes.Host", "seeded log line"));
        var telemetry = new ErrorTelemetryStore(50);
        telemetry.Record("agent", "seeded error");
        return new DiagnosticCollector(log, telemetry, new HostIdentity("h", "Host", "1.0"),
            () => ["claude-code"], maxBytes: 1024 * 1024);
    }

    private static BugReportRouter Router(RecordingSink sink, bool enabled, DiagnosticCollector? collector = null)
    {
        var registry = new PluginRegistry<IBugReportSink>([sink], s => s.Id);
        var policy = new DiagnosticAttachmentPolicy(enabled, id => id == OwnerId);
        return new BugReportRouter(registry, sink.Id, collector, policy);
    }

    [Fact]
    public async Task Owner_opted_in_attaches_the_payload_and_it_reaches_the_sink()
    {
        var sink = new RecordingSink();
        var router = Router(sink, enabled: true, collector: Collector());

        var result = await router.SubmitAsync(Report(), attachDiagnostics: true, callerId: OwnerId);

        Assert.True(result.Success);
        Assert.NotNull(sink.Last!.DiagnosticPayload);
        Assert.Contains("seeded log line", System.Text.Encoding.UTF8.GetString(sink.Last!.DiagnosticPayload!));
    }

    [Fact]
    public async Task Not_opted_in_leaves_the_payload_null()
    {
        var sink = new RecordingSink();
        var router = Router(sink, enabled: true, collector: Collector());

        await router.SubmitAsync(Report(), attachDiagnostics: false, callerId: OwnerId);

        Assert.Null(sink.Last!.DiagnosticPayload);
    }

    [Fact]
    public async Task Opted_in_but_not_the_owner_leaves_the_payload_null()
    {
        var sink = new RecordingSink();
        var router = Router(sink, enabled: true, collector: Collector());

        await router.SubmitAsync(Report(), attachDiagnostics: true, callerId: "some-guest");

        Assert.Null(sink.Last!.DiagnosticPayload);
        Assert.False(router.CanAttachDiagnostics("some-guest"));
        Assert.True(router.CanAttachDiagnostics(OwnerId));
    }

    [Fact]
    public async Task Capability_disabled_leaves_the_payload_null_even_for_the_owner()
    {
        var sink = new RecordingSink();
        var router = Router(sink, enabled: false, collector: Collector());

        await router.SubmitAsync(Report(), attachDiagnostics: true, callerId: OwnerId);

        Assert.Null(sink.Last!.DiagnosticPayload);
        Assert.False(router.CanAttachDiagnostics(OwnerId));
    }

    // ---- owner determination via the real DeviceRegistry ----

    [Fact]
    public void DeviceRegistry_owner_is_the_bootstrap_and_the_earliest_paired_device()
    {
        var reg = new DeviceRegistry("boot-token", _deviceFile);
        var first = reg.TryPair(reg.PairingCode, "laptop")!;
        var second = reg.TryPair(reg.PairingCode, "phone")!;

        Assert.Equal("bootstrap", reg.ResolveCallerId("boot-token"));
        Assert.True(reg.IsOwner("bootstrap"));               // the operator's bootstrap token
        Assert.True(reg.IsOwner(first.DeviceId));            // earliest-paired device
        Assert.False(reg.IsOwner(second.DeviceId));          // a later device is not the owner
        Assert.False(reg.IsOwner(null));
        Assert.False(reg.IsOwner("unknown"));
    }

    // ---- invariant: the browser-fallback path never carries the payload ----

    [Fact]
    public void Browser_fallback_prefill_never_contains_the_diagnostic_payload()
    {
        // A report that DID get a payload attached host-side.
        var withPayload = Report() with { DiagnosticPayload = [1, 2, 3, 4, 5] };

        var url = BugReportPrefill.NewIssueUrl("AdamFrisby/Agnes", withPayload);
        var body = BugReportPrefill.BuildBody(withPayload);

        Assert.DoesNotContain("AQIDBAU", url);   // base64 of the payload bytes
        Assert.DoesNotContain("payload", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQIDBAU", body);
    }

    [Fact]
    public async Task Tokenless_github_fallback_url_never_carries_an_attached_payload()
    {
        // Owner opts in → the router attaches a payload; the sink has no token, so it degrades to a public
        // browser-fallback URL. That URL must still never carry the diagnostic payload.
        var sink = new GitHubIssueSink(new HttpClient(new ThrowingHandler()),
            "AdamFrisby/Agnes", token: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubIssueSink>.Instance);
        var registry = new PluginRegistry<IBugReportSink>([sink], s => s.Id);
        var router = new BugReportRouter(registry, sink.Id, Collector(),
            new DiagnosticAttachmentPolicy(enabled: true, id => id == OwnerId));

        var result = await router.SubmitAsync(Report(), attachDiagnostics: true, callerId: OwnerId);

        Assert.NotNull(result.Url);
        Assert.StartsWith("https://github.com/AdamFrisby/Agnes/issues/new?", result.Url);
        Assert.DoesNotContain("seeded log line", result.Url);
        Assert.DoesNotContain("seeded error", result.Url);
    }

    private sealed class RecordingSink : IBugReportSink
    {
        public BugReport? Last { get; private set; }

        public string Id => "recording";

        public Task<BugReportResult> SubmitAsync(BugReport report, CancellationToken ct = default)
        {
            Last = report;
            return Task.FromResult(new BugReportResult(true, "https://example.test/1", null));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no network without a token");
    }
}
