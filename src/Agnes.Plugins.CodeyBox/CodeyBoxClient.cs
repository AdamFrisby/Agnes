using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Talks to one CodeyBox orchestrator: REST for the queue, SignalR for an agent's live stdout.
/// </summary>
/// <remarks>
/// <para>Its own <see cref="HttpClient"/>, deliberately — <b>not</b> Agnes's <c>AgnesHttp.For(pin)</c>.
/// That exists because an Agnes host is typically self-signed and authenticated by a pinned certificate
/// fingerprint. CodeyBox is a different service with different trust rules (a bearer key over plain HTTP,
/// usually on localhost), so it takes a client of its own; <c>GitHubDeviceLogin</c> is the existing
/// precedent for one flow reaching two services that are trusted differently.</para>
///
/// <para>The hub carries the same bearer token as the REST calls. Its callback is <c>stdoutChunk</c>,
/// scoped to a work item by <c>SubscribeAsync</c> — the server puts the connection in a <c>wi:{id}</c>
/// group, so no filtering is needed here beyond following the right item.</para>
/// </remarks>
public sealed class CodeyBoxClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CodeyBoxOptions _options;
    private readonly HttpClient _http;

    private HubConnection? _hub;
    private string? _following;

    public CodeyBoxClient(CodeyBoxOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(options.BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        if (options.ApiKey is { Length: > 0 } key)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    /// <summary>Raised for each chunk of the followed item's agent output.</summary>
    public event Action<StdoutChunk>? StdoutReceived;

    /// <summary>Raised when the followed item's stream ends.</summary>
    public event Action<string>? StreamCompleted;

    public async Task<IReadOnlyList<WorkItemRow>> ListWorkItemsAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<List<WorkItemRow>>("workitems", Json, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<QueueStatus?> GetQueueStatusAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<QueueStatus>("queue/status", Json, cancellationToken).ConfigureAwait(false);

    /// <summary>The tail of an item's agent output, for the scrollback a live subscription cannot replay.</summary>
    public async Task<string> GetStdoutTailAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"workitems/{workItemId}/stdout-tail", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    public Task CancelAsync(string workItemId, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"workitems/{workItemId}"), cancellationToken);

    public Task RetryAsync(string workItemId, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"workitems/{workItemId}/retry"), cancellationToken);

    public Task PromoteAsync(string workItemId, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"workitems/{workItemId}/promote"), cancellationToken);

    public async Task SetQueuePausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "queue/pause")
        {
            Content = JsonContent.Create(new { paused }, options: Json),
        };
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Follows one work item's agent output, connecting the hub on first use. Following a second item
    /// unsubscribes the first, because the panel shows one at a time and leaving stale groups joined would
    /// interleave two agents' output into it.
    /// </summary>
    public async Task FollowAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        var hub = await EnsureHubAsync(cancellationToken).ConfigureAwait(false);

        if (_following is { } previous && previous != workItemId)
        {
            try
            {
                await hub.InvokeAsync("UnsubscribeAsync", previous, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: a dropped connection has already forgotten its groups.
            }
        }

        _following = workItemId;
        await hub.InvokeAsync("SubscribeAsync", workItemId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HubConnection> EnsureHubAsync(CancellationToken cancellationToken)
    {
        if (_hub is { } existing && existing.State != HubConnectionState.Disconnected)
        {
            return existing;
        }

        if (_hub is null)
        {
            var hub = new HubConnectionBuilder()
                .WithUrl($"{_options.BaseUrl}/hubs/agent-stdout", o =>
                    o.AccessTokenProvider = () => Task.FromResult(_options.ApiKey))
                .WithAutomaticReconnect()
                .Build();

            hub.On<StdoutChunk>("stdoutChunk", chunk => StdoutReceived?.Invoke(chunk));
            hub.On<JsonElement>("streamComplete", payload =>
                StreamCompleted?.Invoke(
                    payload.TryGetProperty("workItemId", out var id) ? id.GetString() ?? string.Empty : string.Empty));

            // A reconnect rejoins nothing on its own — server-side groups die with the connection — so the
            // item being followed is re-subscribed explicitly.
            hub.Reconnected += async _ =>
            {
                if (_following is { } item)
                {
                    try
                    {
                        await hub.InvokeAsync("SubscribeAsync", item).ConfigureAwait(false);
                    }
                    catch
                    {
                        // the next Follow will put it right
                    }
                }
            };

            _hub = hub;
        }

        await _hub.StartAsync(cancellationToken).ConfigureAwait(false);
        return _hub;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is { } hub)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }

        _http.Dispose();
    }
}
