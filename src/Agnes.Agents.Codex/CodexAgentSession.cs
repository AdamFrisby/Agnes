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
    private readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
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

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<QuestionAnswer> answers, CancellationToken cancellationToken = default)
    {
        if (_pendingQuestions.TryGetValue(requestId, out var pending))
        {
            pending.Completion.TrySetResult(answers);
        }
        else
        {
            _logger.LogWarning("Codex question answer for unknown request {RequestId}", requestId);
        }

        return Task.CompletedTask;
    }

    // ---- called by the connection (on the serial dispatch thread) ----

    /// <summary>
    /// Handle an <c>item/tool/requestUserInput</c> server request: surface the questions to the user,
    /// wait for their answers, and return them as the RPC result. Empty answers (dismissed) are echoed
    /// back as empty selections so the turn doesn't hang.
    /// </summary>
    public async Task<CodexRequestUserInputResult> HandleUserInputAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("n");
        var toolCallId = GetString(parameters, "itemId") ?? string.Empty;
        var (questions, order) = ParseQuestions(parameters);

        // No parseable questions — answer empty rather than surfacing an empty card.
        if (questions.Count == 0)
        {
            return new CodexRequestUserInputResult(new Dictionary<string, CodexUserInputAnswer>());
        }

        var tcs = new TaskCompletionSource<IReadOnlyList<QuestionAnswer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQuestions[requestId] = new PendingQuestion(tcs, order);

        Emit(new QuestionAskedEvent(requestId, toolCallId, questions));

        try
        {
            await using (cancellationToken.Register(() => tcs.TrySetResult(Array.Empty<QuestionAnswer>())))
            {
                var answers = await tcs.Task.ConfigureAwait(false);
                Emit(new QuestionAnsweredEvent(requestId));
                return BuildResult(answers, order);
            }
        }
        finally
        {
            _pendingQuestions.TryRemove(requestId, out _);
        }
    }

    /// <summary>Parse Codex's <c>questions[]</c> into Agnes questions, keeping each question's id so the
    /// answer result can be keyed back. Codex answers are always arrays, so questions are single-select by
    /// default (the array carries one label); <c>isOther</c> means free-text notes are accepted.</summary>
    private static (IReadOnlyList<AgentQuestion> Questions, IReadOnlyList<string> Order) ParseQuestions(JsonElement p)
    {
        var questions = new List<AgentQuestion>();
        var order = new List<string>();
        if (!p.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array)
        {
            return (questions, order);
        }

        var index = 0;
        foreach (var q in qs.EnumerateArray())
        {
            var id = GetString(q, "id") ?? $"q{index}";
            var header = GetString(q, "header") ?? string.Empty;
            var prompt = GetString(q, "question") ?? header;
            var allowFreeText = q.TryGetProperty("isOther", out var other) && other.ValueKind == JsonValueKind.True;

            var options = new List<QuestionChoice>();
            if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    var label = GetString(o, "label") ?? string.Empty;
                    if (label.Length == 0)
                    {
                        continue;
                    }

                    options.Add(new QuestionChoice(label, GetString(o, "description") ?? string.Empty));
                }
            }

            questions.Add(new AgentQuestion(id, header, prompt, options, MultiSelect: false, AllowFreeText: allowFreeText || options.Count == 0));
            order.Add(id);
            index++;
        }

        return (questions, order);
    }

    /// <summary>Map the user's answers into Codex's result shape: <c>{answers:{[id]:{answers:string[]}}}</c>.
    /// Free-text notes ride as an extra answer string (Codex's "Other" convention).</summary>
    private static CodexRequestUserInputResult BuildResult(IReadOnlyList<QuestionAnswer> answers, IReadOnlyList<string> order)
    {
        var byId = answers.ToDictionary(a => a.QuestionId, a => a);
        var result = new Dictionary<string, CodexUserInputAnswer>();
        foreach (var id in order)
        {
            var picks = new List<string>();
            if (byId.TryGetValue(id, out var a))
            {
                picks.AddRange(a.SelectedLabels);
                if (!string.IsNullOrWhiteSpace(a.Notes))
                {
                    picks.Add(a.Notes.Trim());
                }
            }

            result[id] = new CodexUserInputAnswer(picks);
        }

        return new CodexRequestUserInputResult(result);
    }

    private sealed record PendingQuestion(TaskCompletionSource<IReadOnlyList<QuestionAnswer>> Completion, IReadOnlyList<string> Order);

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
