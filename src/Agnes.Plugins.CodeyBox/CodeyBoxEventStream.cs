using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Plugins.CodeyBox;

/// <summary>One event off the orchestrator's feed, reduced to what the UI acts on.</summary>
/// <param name="Id">The feed's monotonic sequence number, used to resume without gaps.</param>
/// <param name="Type">Dot-separated name, e.g. <c>work_item.audit_failed</c>.</param>
/// <param name="WorkItemId">Set for <c>work_item.*</c>; null for queue- and agent-level events.</param>
/// <param name="OccurredAt">When the orchestrator generated it — not when we read it. The distinction
/// matters on connect, because the feed replays its buffer.</param>
internal sealed record CodeyBoxEvent(long Id, string Type, string? WorkItemId, string? ProjectId, DateTimeOffset OccurredAt)
{
    public bool IsWorkItem => Type.StartsWith("work_item.", StringComparison.Ordinal);

    public bool IsQueue => Type.StartsWith("queue.", StringComparison.Ordinal);
}

/// <summary>
/// Subscribes to the orchestrator's Server-Sent Events feed (<c>GET /workitems/events</c>) so the queue
/// learns about changes when they happen, rather than by asking every few seconds.
///
/// <para>Polling was wrong twice over. It is late by up to its own interval, and — worse for a person —
/// it rebuilt the list on a timer whether or not anything had moved, so a queue could not be read while
/// it was open. The feed removes both: nothing is rebuilt unless the orchestrator says something
/// actually changed.</para>
///
/// <para><b>The buffer replays.</b> A client that connects without a cursor receives the broadcaster's
/// whole ring buffer — on this host that meant events from four days earlier arriving as though new.
/// They are not noise to be tolerated: acting on them would refetch hundreds of items that the initial
/// snapshot already covers. So the caller stamps the moment it took that snapshot and anything older is
/// dropped, which is sound because the snapshot already contains those effects. After the first
/// connection the cursor takes over and the question does not arise again.</para>
///
/// <para>Reconnects resume from <c>Last-Event-ID</c>, which is what makes a dropped connection lossless
/// rather than merely survivable: the server replays from the cursor, so an event that landed while we
/// were away is still delivered. Backoff is capped — a feed that is down is not an error to report on
/// every attempt, and the queue keeps working from its last known state either way.</para>
/// </summary>
internal sealed class CodeyBoxEventStream(CodeyBoxOptions options, Func<HttpClient>? clientFactory = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    private long? _cursor;

    /// <summary>
    /// Reads the feed until cancelled, reconnecting as needed. Each event is handed to
    /// <paramref name="onEvent"/> in arrival order.
    /// </summary>
    /// <param name="notBefore">Events generated before this are dropped — see the replay note above.</param>
    /// <param name="onConnected">Called each time the stream (re-)opens. A reconnect may have missed
    /// events the buffer no longer holds, so the caller reconciles against a full read there.</param>
    public async Task RunAsync(
        DateTimeOffset notBefore,
        Func<CodeyBoxEvent, Task> onEvent,
        Func<bool, Task>? onConnected,
        CancellationToken cancellationToken)
    {
        var retry = FirstRetry;
        var reconnect = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadOnceAsync(notBefore, onEvent, onConnected, reconnect, cancellationToken).ConfigureAwait(false);

                // A clean end of stream is still a disconnect: loop and re-open.
                retry = FirstRetry;
                reconnect = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                reconnect = true;
                Diagnostic.Report("event-stream", ex);
            }

            try
            {
                await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retry = retry < MaxRetry ? retry + retry : MaxRetry;
        }
    }

    private async Task ReadOnceAsync(
        DateTimeOffset notBefore,
        Func<CodeyBoxEvent, Task> onEvent,
        Func<bool, Task>? onConnected,
        bool reconnect,
        CancellationToken cancellationToken)
    {
        using var http = clientFactory?.Invoke() ?? new HttpClient();

        // No overall timeout: the point of this connection is to stay open indefinitely. A dead peer is
        // caught by the server's :keepalive comments failing to arrive, not by a request deadline.
        http.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, options.BaseUrl.TrimEnd('/') + "/workitems/events");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (options.ApiKey is { Length: > 0 } key)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        if (_cursor is { } cursor)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (onConnected is not null)
        {
            await onConnected(reconnect).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        long? id = null;
        string? type = null;
        var data = new System.Text.StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (line.Length == 0)
            {
                // Blank line ends a frame.
                if (type is { Length: > 0 } && data.Length > 0)
                {
                    var parsed = Parse(id, type, data.ToString());
                    if (parsed is not null)
                    {
                        // The cursor advances on every frame we accept, including ones we then drop as
                        // replay — they ARE delivered, and resuming from before them would replay them
                        // all over again on the next reconnect.
                        _cursor = parsed.Id > 0 ? parsed.Id : _cursor;

                        if (parsed.OccurredAt >= notBefore)
                        {
                            await onEvent(parsed).ConfigureAwait(false);
                        }
                    }
                }

                id = null;
                type = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;   // comment, e.g. the :keepalive heartbeat
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');

            switch (field)
            {
                case "id" when long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsedId):
                    id = parsedId;
                    break;
                case "event":
                    type = value;
                    break;
                case "data":
                    // Multi-line data fields are joined with newlines, per the SSE spec.
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
                default:
                    break;   // "retry" and unknown fields are not used here
            }
        }
    }

    private static CodeyBoxEvent? Parse(long? id, string type, string data)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<EventEnvelope>(data, Json);
            return new CodeyBoxEvent(
                id ?? 0,
                payload?.EventType ?? type,
                payload?.WorkItem?.Id,
                payload?.Project?.Id,
                payload?.OccurredAt ?? DateTimeOffset.MinValue);
        }
        catch (JsonException ex)
        {
            // A frame we cannot read is not a reason to tear down the subscription.
            Diagnostic.Report("event-stream-parse", ex);
            return null;
        }
    }

    /// <summary>The envelope's fields we act on. The feed carries considerably more (usage, revisions,
    /// release state); it is deliberately not modelled here, because this type exists to answer "what
    /// changed" and the answer is then read from the API in full.</summary>
    private sealed record EventEnvelope
    {
        [JsonPropertyName("eventType")]
        public string? EventType { get; init; }

        [JsonPropertyName("occurredAt")]
        public DateTimeOffset? OccurredAt { get; init; }

        [JsonPropertyName("workItem")]
        public EventWorkItem? WorkItem { get; init; }

        [JsonPropertyName("project")]
        public EventProject? Project { get; init; }
    }

    private sealed record EventWorkItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }

    private sealed record EventProject
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
