using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Acp.Wire;
using Microsoft.Extensions.Logging;

namespace Agnes.Acp;

/// <summary>Callback surface the session uses to send requests to the agent process.</summary>
internal interface IAcpRpc
{
    Task<AcpPromptResult> PromptAsync(AcpPromptParams parameters, CancellationToken cancellationToken);
    Task CancelAsync(AcpCancelParams parameters);
}

/// <summary>An <see cref="IAgentSession"/> backed by one ACP session on a connected agent process.</summary>
internal sealed class AcpAgentSession : IAgentSession
{
    private readonly IAcpRpc _rpc;
    private readonly ILogger _logger;
    private readonly SynchronizationContext _dispatch;
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, PendingPermission> _pending = new();

    public AcpAgentSession(string agentSessionId, IAcpRpc rpc, SynchronizationContext dispatch, ILogger logger)
    {
        AgentSessionId = agentSessionId;
        _rpc = rpc;
        _dispatch = dispatch;
        _logger = logger;
    }

    public string AgentSessionId { get; }

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
    {
        var result = await _rpc.PromptAsync(
            new AcpPromptParams
            {
                SessionId = AgentSessionId,
                Prompt = content.Select(AcpMap.FromContent).ToArray(),
            },
            cancellationToken).ConfigureAwait(false);

        var reason = AcpMap.ToStopReason(result.StopReason);

        // Emit turn-end through the connection's serial dispatch queue so it is ordered
        // AFTER the session/update notifications that preceded the response on the wire
        // (the request completion runs off the dispatch thread and would otherwise race ahead).
        _dispatch.Post(_ => Emit(new TurnEndedEvent(reason)), null);
        return reason;
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
        => _rpc.CancelAsync(new AcpCancelParams { SessionId = AgentSessionId });

    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
    {
        if (_pending.TryGetValue(requestId, out var pending))
        {
            pending.Tcs.TrySetResult(new AcpPermissionOutcome { Outcome = "selected", OptionId = optionId });
        }
        else
        {
            _logger.LogWarning("Permission response for unknown request {RequestId}", requestId);
        }

        return Task.CompletedTask;
    }

    // ---- called by the connection ----

    public void HandleUpdate(JsonElement update)
    {
        foreach (var e in AcpMap.ToEvents(update))
        {
            Emit(e);
        }
    }

    public async Task<AcpRequestPermissionResult> HandlePermissionRequestAsync(
        AcpRequestPermissionParams parameters,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<AcpPermissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingPermission(tcs, parameters.Options);
        _pending[requestId] = pending;

        Emit(new PermissionRequestedEvent(
            requestId,
            parameters.ToolCall?.ToolCallId ?? string.Empty,
            parameters.ToolCall?.Title ?? "Permission required",
            parameters.Options
                .Select(o => new Abstractions.PermissionOption(o.OptionId, o.Name, AcpMap.ToOptionKind(o.Kind)))
                .ToArray()));

        try
        {
            await using (cancellationToken.Register(() =>
                tcs.TrySetResult(new AcpPermissionOutcome { Outcome = "cancelled" })))
            {
                var outcome = await tcs.Task.ConfigureAwait(false);
                Emit(new PermissionResolvedEvent(requestId, outcome.OptionId, ResolveOutcome(outcome, parameters.Options)));
                return new AcpRequestPermissionResult { Outcome = outcome };
            }
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private static PermissionOutcome ResolveOutcome(AcpPermissionOutcome outcome, IReadOnlyList<AcpPermissionOption> options)
    {
        if (outcome.Outcome == "cancelled")
        {
            return PermissionOutcome.Cancelled;
        }

        var kind = options.FirstOrDefault(o => o.OptionId == outcome.OptionId)?.Kind;
        return kind is "reject_once" or "reject_always" ? PermissionOutcome.Denied : PermissionOutcome.Allowed;
    }

    private void Emit(SessionEvent e)
    {
        if (!_events.Writer.TryWrite(e with { Timestamp = DateTimeOffset.UtcNow }))
        {
            _logger.LogWarning("Dropped event for session {SessionId}", AgentSessionId);
        }
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.Tcs.TrySetResult(new AcpPermissionOutcome { Outcome = "cancelled" });
        }

        return ValueTask.CompletedTask;
    }

    private sealed record PendingPermission(
        TaskCompletionSource<AcpPermissionOutcome> Tcs,
        IReadOnlyList<AcpPermissionOption> Options);
}
