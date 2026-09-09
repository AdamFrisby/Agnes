using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.OpenCode.Native;

/// <summary>
/// One OpenCode session driven over the native HTTP API, with its event stream consumed as SSE.
/// </summary>
/// <remarks>
/// Where the ACP path blocks on a <c>session/prompt</c> response for the whole turn, here the prompt returns
/// immediately and the turn's shape is read off the event stream. That is what makes an interrupt real
/// (<c>POST /interrupt</c> rather than hoping the agent notices a cancel) and what lets a failed step be
/// reported as a failure instead of a clean end.
/// </remarks>
internal sealed class OpenCodeNativeSession : IAgentSession
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _unknownTypes = [];
    private readonly Task _pump;

    /// <summary>Completed when the current turn's step ends, so PromptAsync can report a stop reason the
    /// way the rest of Agnes expects.</summary>
    private TaskCompletionSource<StopReason>? _turn;

    public OpenCodeNativeSession(string sessionId, HttpClient http, ILogger logger)
    {
        AgentSessionId = sessionId;
        _http = http;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    public string AgentSessionId { get; }

    public IReadOnlyList<SessionMode> Modes { get; } = [];

    public string? CurrentModeId => null;

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
    {
        var text = string.Join("\n", content.OfType<TextContent>().Select(t => t.Text));
        var turn = new TaskCompletionSource<StopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = turn;

        // "queue" rather than "steer": a second prompt should follow the first, not cut into it. Agnes's own
        // send policy already decides whether to queue or interrupt, and interrupting has its own call.
        var response = await _http.PostAsJsonAsync(
            $"/api/session/{AgentSessionId}/prompt",
            new { prompt = new { text }, delivery = "queue" },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var registration = cancellationToken.Register(() => turn.TrySetResult(StopReason.Cancelled));
        return await turn.Task.ConfigureAwait(false);
    }

    /// <summary>A real interrupt, rather than a cancel the agent may or may not act on.</summary>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _http.PostAsync($"/api/session/{AgentSessionId}/interrupt", content: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interrupt failed for opencode session {SessionId}", AgentSessionId);
        }
    }

    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
        => _http.PostAsJsonAsync(
            $"/api/session/{AgentSessionId}/permission/{requestId}/reply",
            new { reply = optionId },
            cancellationToken);

    public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // native OpenCode has agents rather than ACP modes

    /// <summary>Switches the model without relaunching the agent — the thing the ACP path cannot do.</summary>
    public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
        => _http.PostAsJsonAsync($"/api/session/{AgentSessionId}/model", new { model = modelId }, cancellationToken);

    private async Task PumpAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/session/{AgentSessionId}/event");
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
            {
                // SSE: only data lines carry payload; blank lines and comments frame the stream.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                Dispatch(line["data:".Length..].Trim());
            }
        }
        catch (OperationCanceledException)
        {
            // Session disposed — an intentional stop.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "opencode event stream failed for session {SessionId}", AgentSessionId);
            _turn?.TrySetResult(StopReason.EndTurn); // never leave a prompt awaiting forever
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private void Dispatch(string payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        JsonElement element;
        try
        {
            element = JsonDocument.Parse(payload).RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // a frame we can't parse tells us nothing; the stream continues
        }

        var type = element.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (!OpenCodeEventMap.IsKnown(type) && _unknownTypes.Add(type ?? string.Empty))
        {
            _logger.LogWarning(
                "opencode sent an unmodelled event '{EventType}' for session {SessionId} (logged once per type)",
                type, AgentSessionId);
        }

        foreach (var e in OpenCodeEventMap.ToEvents(element))
        {
            _events.Writer.TryWrite(e);
        }

        // A step ending — cleanly or not — is what ends a turn here. step.failed still ends it, but the
        // AgentErrorEvent the mapper emitted alongside is what says the turn failed rather than finished.
        if (type is "session.next.step.ended" or "session.next.step.failed")
        {
            var reason = type is "session.next.step.failed" ? StopReason.Refusal : StopReason.EndTurn;
            _events.Writer.TryWrite(new TurnEndedEvent(reason, type));
            _turn?.TrySetResult(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal must not surface the pump's shutdown noise.
        }

        _cts.Dispose();
    }
}
