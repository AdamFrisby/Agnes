using System.Net;
using System.Text;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The SSE reader, against frames captured verbatim from the running orchestrator. The queue's freshness
/// now depends on this parser, so the shapes it must survive are pinned rather than assumed: heartbeat
/// comments, an event whose payload carries no work item, and the buffer replay that arrives on connect.
/// </summary>
public sealed class EventStreamTests
{
    /// <summary>Two frames as the orchestrator actually emitted them, keepalive included.</summary>
    private const string Captured =
        ":keepalive\n\n" +
        "id: 1\n" +
        "event: upstream.pr_stale_base\n" +
        "data: {\"eventSchemaVersion\":\"1.5\",\"eventType\":\"upstream.pr_stale_base\"," +
        "\"occurredAt\":\"2026-08-24T20:45:26.4762302+00:00\"," +
        "\"project\":{\"id\":\"codeybox-self\",\"displayName\":\"CodeyBox (self-modify)\"}}\n" +
        "\n" +
        "id: 2\n" +
        "event: work_item.audit_failed\n" +
        "data: {\"eventSchemaVersion\":\"1.5\",\"eventType\":\"work_item.audit_failed\"," +
        "\"occurredAt\":\"2026-08-27T22:43:00.2019839+00:00\"," +
        "\"workItem\":{\"id\":\"3f2b1c00-0000-0000-0000-000000000001\",\"state\":\"Failed\"}," +
        "\"project\":{\"id\":\"codeybox-self\"}}\n" +
        "\n";

    private sealed class CannedHandler(string body, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
            });
        }
    }

    /// <summary>
    /// Reads the canned body, then stops on the RE-connect rather than on a timer. The stream only
    /// re-opens once the first response is fully consumed, so this is deterministic; an earlier version
    /// cancelled after 50ms and flaked whenever writing a diagnostic stack trace took longer than that.
    /// </summary>
    private static async Task<List<CodeyBoxEvent>> ReadAsync(
        string body,
        DateTimeOffset notBefore,
        Action<HttpRequestMessage>? inspect = null,
        int stopAfterConnects = 2)
    {
        var seen = new List<CodeyBoxEvent>();
        var connects = 0;
        using var cts = new CancellationTokenSource();
        var handler = new CannedHandler(body, inspect);
        var stream = new CodeyBoxEventStream(
            new CodeyBoxOptions("http://localhost:5836", "k"),
            () => new HttpClient(handler, disposeHandler: false));

        await stream.RunAsync(
            notBefore,
            evt => { seen.Add(evt); return Task.CompletedTask; },
            _ =>
            {
                if (++connects >= stopAfterConnects)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            cts.Token);

        return seen;
    }

    [Fact]
    public async Task ParsesCapturedFramesAndIgnoresTheHeartbeat()
    {
        var events = await ReadAsync(Captured, DateTimeOffset.MinValue);

        Assert.Equal(2, events.Count);
        Assert.Equal("upstream.pr_stale_base", events[0].Type);
        Assert.Null(events[0].WorkItemId);
        Assert.False(events[0].IsWorkItem);

        Assert.Equal("work_item.audit_failed", events[1].Type);
        Assert.True(events[1].IsWorkItem);
        Assert.Equal("3f2b1c00-0000-0000-0000-000000000001", events[1].WorkItemId);
        Assert.Equal(2, events[1].Id);
    }

    [Fact]
    public async Task DropsTheReplayedBufferThatPredatesTheSnapshot()
    {
        // The live feed replayed events from four days before connecting. Acting on those would refetch
        // items the initial read already covered.
        var events = await ReadAsync(Captured, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        var only = Assert.Single(events);
        Assert.Equal("work_item.audit_failed", only.Type);
    }

    [Fact]
    public async Task ResumesFromTheCursorEvenForEventsItDropped()
    {
        // A dropped replay is still DELIVERED. Resuming from before it would replay the whole buffer
        // again on every reconnect — a loop, not a recovery.
        var resumeHeaders = new List<string?>();
        await ReadAsync(
            Captured,
            DateTimeOffset.MaxValue,           // drop everything
            inspect: r => resumeHeaders.Add(
                r.Headers.TryGetValues("Last-Event-ID", out var v) ? string.Join(",", v) : null),
            stopAfterConnects: 2);

        Assert.Null(resumeHeaders[0]);          // first connect asks for no cursor
        Assert.Equal("2", resumeHeaders[1]);    // reconnect resumes past both frames
    }

    [Fact]
    public async Task ReportsAReconnectSoTheCallerCanReconcile()
    {
        var connects = new List<bool>();
        using var cts = new CancellationTokenSource();
        var handler = new CannedHandler(Captured);
        var stream = new CodeyBoxEventStream(
            new CodeyBoxOptions("http://localhost:5836", "k"),
            () => new HttpClient(handler, disposeHandler: false));

        await stream.RunAsync(
            DateTimeOffset.MinValue,
            _ => Task.CompletedTask,
            reconnected =>
            {
                connects.Add(reconnected);
                if (connects.Count >= 2)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            cts.Token);

        Assert.False(connects[0]);   // the first open is not a reconnect; the caller already read the queue
        Assert.True(connects[1]);    // a re-open is, so the caller re-reads
    }

    [Fact]
    public async Task AMalformedFrameDoesNotKillTheSubscription()
    {
        var body =
            "id: 1\nevent: work_item.failed\ndata: {not json\n\n" +
            "id: 2\nevent: work_item.done\ndata: {\"eventType\":\"work_item.done\"," +
            "\"occurredAt\":\"2026-08-27T22:43:00Z\",\"workItem\":{\"id\":\"abc\"}}\n\n";

        var events = await ReadAsync(body, DateTimeOffset.MinValue);

        var only = Assert.Single(events);
        Assert.Equal("abc", only.WorkItemId);
    }
}
