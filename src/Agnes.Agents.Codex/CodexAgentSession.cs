using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Agents.Codex.Wire;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Codex;

/// <summary>Outbound calls a session makes to the Codex app-server (via the connection).</summary>
internal interface ICodexRpc
{
    Task<string> StartTurnAsync(string threadId, IReadOnlyList<CodexUserInput> input, CancellationToken cancellationToken);
    Task InterruptAsync(string threadId);
}

/// <summary>
/// An <see cref="IAgentSession"/> backed by one Codex thread on a connected <c>codex app-server</c>.
/// A prompt drives one <c>turn/start</c> and completes when the matching <c>turn/completed</c>
/// notification arrives (prompts are serial per session, so a single active-turn slot suffices).
/// </summary>
internal sealed class CodexAgentSession : IAgentSession
{
    private readonly ICodexRpc _rpc;
    private readonly ILogger _logger;
    private readonly CodexMap _map = new();
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private TaskCompletionSource<StopReason>? _activeTurn;

    public CodexAgentSession(string threadId, ICodexRpc rpc, ILogger logger)
    {
        AgentSessionId = threadId;
        _rpc = rpc;
        _logger = logger;
    }

    public string AgentSessionId { get; }

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<StopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeTurn = tcs;
        try
        {
            await _rpc.StartTurnAsync(AgentSessionId, CodexMap.ToInput(content), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _activeTurn = null;
            throw;
        }

        await using (cancellationToken.Register(() => tcs.TrySetResult(StopReason.Cancelled)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken = default) => _rpc.InterruptAsync(AgentSessionId);

    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
    {
        if (_pendingApprovals.TryGetValue(requestId, out var pending))
        {
            pending.TrySetResult(optionId == "approve");
        }
        else
        {
            _logger.LogWarning("Codex approval response for unknown request {RequestId}", requestId);
        }

        return Task.CompletedTask;
    }

    // ---- called by the connection (on the serial dispatch thread) ----

    public void HandleItemStarted(JsonElement notification)
    {
        foreach (var e in _map.ItemStarted(notification))
        {
            Emit(e);
        }
    }

    public void HandleItemCompleted(JsonElement notification)
    {
        foreach (var e in _map.ItemCompleted(notification))
        {
            Emit(e);
        }
    }

    public void HandleAgentMessageDelta(JsonElement notification)
    {
        if (_map.AgentMessageDelta(notification) is { } e)
        {
            Emit(e);
        }
    }

    public void HandleTokenUsage(JsonElement notification)
    {
        if (_map.TokenUsage(notification) is { } e)
        {
            Emit(e);
        }
    }

    public void HandleError(JsonElement notification)
    {
        var message = notification.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
            && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : "Codex reported an error.";
        Emit(new AgentErrorEvent(message ?? "Codex reported an error."));
    }

    public void HandleTurnCompleted(JsonElement notification)
    {
        var status = notification.TryGetProperty("turn", out var turn) && turn.TryGetProperty("status", out var s)
            && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        var reason = CodexMap.ToStopReason(status);
        Emit(new TurnEndedEvent(reason));
        _activeTurn?.TrySetResult(reason);
    }

    public async Task<CodexApprovalResponse> HandleApprovalAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("n");
        var toolCallId = GetString(parameters, "callId") ?? string.Empty;
        var title = ApprovalTitle(parameters);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[requestId] = tcs;

        Emit(new PermissionRequestedEvent(requestId, toolCallId, title,
        [
            new PermissionOption("approve", "Approve", PermissionOptionKind.AllowOnce),
            new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce),
        ]));

        try
        {
            await using (cancellationToken.Register(() => tcs.TrySetResult(false)))
            {
                var allow = await tcs.Task.ConfigureAwait(false);
                Emit(new PermissionResolvedEvent(requestId, allow ? "approve" : "deny",
                    allow ? PermissionOutcome.Allowed : PermissionOutcome.Denied));
                return new CodexApprovalResponse(CodexMap.Decision(allow));
            }
        }
        finally
        {
            _pendingApprovals.TryRemove(requestId, out _);
        }
    }

    private static string ApprovalTitle(JsonElement p)
    {
        if (GetString(p, "reason") is { Length: > 0 } reason)
        {
            return reason;
        }

        if (p.TryGetProperty("command", out var c))
        {
            if (c.ValueKind == JsonValueKind.String)
            {
                return $"Run: {c.GetString()}";
            }

            if (c.ValueKind == JsonValueKind.Array)
            {
                return "Run: " + string.Join(' ', c.EnumerateArray().Select(e => e.GetString()));
            }
        }

        return "Approval required";
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private void Emit(SessionEvent e)
    {
        if (!_events.Writer.TryWrite(e with { Timestamp = DateTimeOffset.UtcNow }))
        {
            _logger.LogWarning("Dropped Codex event for thread {ThreadId}", AgentSessionId);
        }
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _activeTurn?.TrySetResult(StopReason.Cancelled);
        foreach (var pending in _pendingApprovals.Values)
        {
            pending.TrySetResult(false);
        }

        return ValueTask.CompletedTask;
    }
}
