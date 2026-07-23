using System.Text;
using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Host.Ops;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Tests;

public class DiagnosticCollectorTests
{
    private static readonly HostIdentity Identity = new("host-123", "Test Host", "9.9.9");

    private static DiagnosticCollector Collector(HostLogRingBuffer log, ErrorTelemetryStore telemetry, long maxBytes)
        => new(log, telemetry, Identity, () => ["claude-code", "codex"], maxBytes);

    [Fact]
    public async Task Collect_assembles_seeded_log_lines_errors_and_metadata_within_the_cap()
    {
        var log = new HostLogRingBuffer(50);
        log.Add(new HostLogLine(DateTimeOffset.UtcNow, LogLevel.Warning, "Agnes.Host.Sessions", "a session hiccup"));
        log.Add(new HostLogLine(DateTimeOffset.UtcNow, LogLevel.Information, "Agnes.Host", "listening on port 5001"));

        var telemetry = new ErrorTelemetryStore(50);
        // Seed via the spine observer path — an AgentErrorEvent must land in the bundle.
        await telemetry.ObserveAsync(new AgentErrorEvent("adapter exploded"));

        var bytes = Collector(log, telemetry, maxBytes: 1024 * 1024).Collect();
        var text = Encoding.UTF8.GetString(bytes);

        Assert.True(bytes.LongLength <= 1024 * 1024);
        Assert.Contains("Test Host", text);            // host metadata
        Assert.Contains("9.9.9", text);                // version
        Assert.Contains("claude-code", text);          // adapter list
        Assert.Contains("codex", text);
        Assert.Contains("adapter exploded", text);     // seeded telemetry
        Assert.Contains("a session hiccup", text);     // seeded log line
        Assert.Contains("listening on port 5001", text);
    }

    [Fact]
    public void Collect_truncates_oversized_content_to_the_cap()
    {
        var log = new HostLogRingBuffer(500);
        // Far more log content than the tiny cap can hold.
        for (var i = 0; i < 500; i++)
        {
            log.Add(new HostLogLine(DateTimeOffset.UtcNow, LogLevel.Information, "Agnes.Host", new string('x', 200)));
        }

        const long cap = 256;
        var bytes = Collector(log, new ErrorTelemetryStore(10), cap).Collect();

        Assert.True(bytes.LongLength <= cap, $"payload {bytes.LongLength} bytes exceeded the {cap}-byte cap");
        // Still valid UTF-8 (truncated on a whole-character boundary), so decoding round-trips cleanly.
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)));
    }
}
