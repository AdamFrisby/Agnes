using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>
/// A host-side session: bridges one agent session to the durable event log and the
/// broadcast fan-out. Pumps the agent's event stream, assigns sequence via the store,
/// and publishes each stored event to subscribed clients.
/// </summary>
internal sealed class HostSession : IAsyncDisposable
{
    private readonly IAgentSession _agent;
    private readonly IEventStore _store;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private int _faultSignalled;

    // Host-originated permission requests (e.g. the credential broker asking to push) awaiting a
    // client's answer — kept apart from agent-originated ones, which are answered by the agent.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _hostPermissions = new();

    private readonly Agnes.Abstractions.Events.IEventBus _bus;

    // The session's single pending-message queue + discarded list, owned host-side (never per-client), plus
    // the current send policy and turn-active flag. All guarded by _queueGate — kept local and explicit to
    // this session rather than any ambient/shared state. Queue mutations are published as a single
    // PendingQueueEvent snapshot (see PublishQueueAsync), so multi-client sync rides the event log for free.
    private readonly object _queueGate = new();
    private readonly List<PendingMessage> _queue = [];
    private readonly List<PendingMessage> _discarded = [];
    private bool _turnActive;
    private SendPolicy _sendPolicy = SendPolicy.QueueInAgent;

    public HostSession(
        string sessionId,
        string adapterId,
        string workingDirectory,
        IAgentSession agent,
        IEventStore store,
        ISessionBroadcaster broadcaster,
        ILogger logger,
        Agnes.Abstractions.Events.IEventBus? bus = null)
    {
        SessionId = sessionId;
        AdapterId = adapterId;
        WorkingDirectory = workingDirectory;
        _agent = agent;
        _store = store;
        _broadcaster = broadcaster;
        _logger = logger;
        _bus = bus ?? new Agnes.Abstractions.Events.EventBus();
        _pump = Task.Run(PumpAsync);
    }

    public string SessionId { get; }
    public string AdapterId { get; }
    public string WorkingDirectory { get; }

    /// <summary>Invoked once if the agent's event stream ends while we did NOT ask it to stop — i.e. the
    /// underlying CLI process died. The host uses this to auto-restart (and resume) the agent.</summary>
    public Action? Faulted { get; set; }

    /// <summary>Invoked with the agent's real session id when it reports one (a native CLI's <c>init</c>
    /// line). Persisted to the catalogue so the agent can be resumed (<c>--resume</c>) reliably — captured
    /// here, not by polling after a prompt, which races the init line and grabs the placeholder id.</summary>
    public Action<string>? AgentSessionStarted { get; set; }

    /// <summary>Invoked when a turn ends — the host uses it to refresh the agent's auto-generated title.</summary>
    public Action? TurnCompleted { get; set; }

    /// <summary>Invoked with the message of an agent error — the host uses it to auto-recover a revoked
    /// Claude OAuth token (relaunch with freshly-materialized credentials).</summary>
    public Action<string>? AgentError { get; set; }

    /// <summary>The agent's own session id (used to resume it after a host restart).</summary>
    public string AgentSessionId => _agent.AgentSessionId;

    // A forked session's seed: the parent's transcript context, prepended to the FIRST real prompt's agent
    // call so the agent has the branch's history — but never logged as a visible user message (see
    // ForkedFromEvent). Cleared after it's consumed once.
    private IReadOnlyList<ContentBlock>? _pendingSeed;

    /// <summary>Sets the fork seed prepended (invisibly) to this session's next prompt.</summary>
    public void SetPendingSeed(IReadOnlyList<ContentBlock> seed) => _pendingSeed = seed;

    /// <summary>Whether an agent turn is currently in flight (set on prompt, cleared on <see cref="TurnEndedEvent"/>).</summary>
    public bool IsTurnActive { get { lock (_queueGate) { return _turnActive; } } }

    /// <summary>The send policy applied to a busy-send (see <see cref="SubmitAsync"/>). Defaults to
    /// <see cref="SendPolicy.QueueInAgent"/>.</summary>
    public SendPolicy SendPolicy
    {
        get { lock (_queueGate) { return _sendPolicy; } }
        set { lock (_queueGate) { _sendPolicy = value; } }
    }

    /// <summary>Records a user prompt in the log, then drives an agent turn in the background. This is the
    /// unconditional immediate send — it starts a turn regardless of the send policy (the policy is applied
    /// by <see cref="SubmitAsync"/>, and this is also the auto-send target when a queued message is drained).</summary>
    public async Task PromptAsync(IReadOnlyList<ContentBlock> content)
    {
        lock (_queueGate)
        {
            _turnActive = true;
        }

        foreach (var block in content)
        {
            await AppendAndPublishAsync(new MessageChunkEvent(MessageRole.User, block)).ConfigureAwait(false);
        }

        // The agent receives the fork seed ahead of the user's message, but only the user's message was
        // logged above — the seed is context transfer, not something a person typed.
        var toAgent = content;
        if (_pendingSeed is { } seed)
        {
            toAgent = [.. seed, .. content];
            _pendingSeed = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _agent.PromptAsync(toAgent, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Prompt failed for session {SessionId}", SessionId);
                await AppendAndPublishAsync(new AgentErrorEvent(ex.Message)).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Applies the session's <see cref="SendPolicy"/> to a user-submitted message — the single policy seam,
    /// so every client and both the idle/busy races resolve the same way host-side:
    /// <list type="bullet">
    /// <item><see cref="SendPolicy.QueueInAgent"/>: queue while a turn is active, else send immediately.</item>
    /// <item><see cref="SendPolicy.PendingUntilReady"/>: always queue (never auto-sends).</item>
    /// <item><see cref="SendPolicy.InterruptAndSend"/>: cancel the in-flight turn then send now.</item>
    /// </list>
    /// </summary>
    public async Task SubmitAsync(IReadOnlyList<ContentBlock> content)
    {
        bool queued;
        bool interrupt;
        lock (_queueGate)
        {
            if (_sendPolicy == SendPolicy.PendingUntilReady || (_sendPolicy == SendPolicy.QueueInAgent && _turnActive))
            {
                _queue.Add(new PendingMessage(Guid.NewGuid().ToString("n"), content));
                queued = true;
                interrupt = false;
            }
            else
            {
                queued = false;
                interrupt = _sendPolicy == SendPolicy.InterruptAndSend && _turnActive;
            }
        }

        if (queued)
        {
            await PublishQueueAsync().ConfigureAwait(false);
            return;
        }

        if (interrupt)
        {
            // Cancel-then-resend: the always-available steering fallback. True mid-turn injection
            // (ISteerableSession) is deferred — no adapter's CLI supports receiving new input mid-turn yet.
            await CancelAsync().ConfigureAwait(false);
        }

        await PromptAsync(content).ConfigureAwait(false);
    }

    /// <summary>Enqueues a message unconditionally (used by "send now" reinsertion / policy-agnostic paths).</summary>
    public Task EnqueueAsync(IReadOnlyList<ContentBlock> content)
    {
        lock (_queueGate)
        {
            _queue.Add(new PendingMessage(Guid.NewGuid().ToString("n"), content));
        }

        return PublishQueueAsync();
    }

    /// <summary>Removes a queued message by id (no-op if it already left the queue).</summary>
    public Task RemovePendingAsync(string messageId)
    {
        bool removed;
        lock (_queueGate)
        {
            var index = _queue.FindIndex(m => m.Id == messageId);
            removed = index >= 0;
            if (removed)
            {
                _queue.RemoveAt(index);
            }
        }

        return removed ? PublishQueueAsync() : Task.CompletedTask;
    }

    /// <summary>Moves a queued message to <paramref name="newIndex"/> (clamped into range).</summary>
    public Task ReorderPendingAsync(string messageId, int newIndex)
    {
        bool moved;
        lock (_queueGate)
        {
            var index = _queue.FindIndex(m => m.Id == messageId);
            moved = index >= 0;
            if (moved)
            {
                var target = Math.Clamp(newIndex, 0, _queue.Count - 1);
                var item = _queue[index];
                _queue.RemoveAt(index);
                _queue.Insert(target, item);
            }
        }

        return moved ? PublishQueueAsync() : Task.CompletedTask;
    }

    /// <summary>Interrupts the current turn (cancel-then-resend) and sends the named queued message ahead of
    /// the rest of the queue. No-op if the message already left the queue.</summary>
    public async Task SendPendingNowAsync(string messageId)
    {
        PendingMessage? message;
        bool wasActive;
        lock (_queueGate)
        {
            var index = _queue.FindIndex(m => m.Id == messageId);
            if (index < 0)
            {
                message = null;
                wasActive = false;
            }
            else
            {
                message = _queue[index];
                _queue.RemoveAt(index);
                wasActive = _turnActive;
            }
        }

        if (message is null)
        {
            return;
        }

        await PublishQueueAsync().ConfigureAwait(false); // reflect the removal from the queue first
        if (wasActive)
        {
            await CancelAsync().ConfigureAwait(false);
        }

        await PromptAsync(message.Content).ConfigureAwait(false);
    }

    /// <summary>Moves every still-queued message to the discarded list (visible, never silently dropped) and
    /// publishes the snapshot. Called when the session is torn down while the queue is non-empty.</summary>
    public async Task DiscardQueuedAsync()
    {
        bool changed;
        lock (_queueGate)
        {
            changed = _queue.Count > 0;
            if (changed)
            {
                _discarded.AddRange(_queue);
                _queue.Clear();
            }
        }

        if (changed)
        {
            try
            {
                await PublishQueueAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort during teardown: the store/broadcaster may already be shutting down. The
                // messages are still preserved in the in-memory discarded list; only the re-broadcast is lost.
                _logger.LogDebug(ex, "Publishing discarded queue failed during teardown for session {SessionId}", SessionId);
            }
        }
    }

    // A single snapshot event per change keeps every client consistent without a bespoke queue-sync
    // channel — a joining/replaying client simply converges on the latest PendingQueueEvent in the log.
    private Task PublishQueueAsync()
    {
        PendingQueueEvent snapshot;
        lock (_queueGate)
        {
            snapshot = new PendingQueueEvent([.. _queue], [.. _discarded]);
        }

        return AppendAndPublishAsync(snapshot);
    }

    // Turn just ended: clear the busy flag, and under the default QueueInAgent policy auto-send the head of
    // the queue (seamlessly continuing into the next turn so a concurrent submit can't slip in between).
    private async Task OnTurnEndedAsync()
    {
        PendingMessage? next;
        lock (_queueGate)
        {
            if (_sendPolicy == SendPolicy.QueueInAgent && _queue.Count > 0)
            {
                next = _queue[0];
                _queue.RemoveAt(0);
                _turnActive = true; // continue straight into the drained message's turn
            }
            else
            {
                next = null;
                _turnActive = false;
            }
        }

        if (next is not null)
        {
            await PublishQueueAsync().ConfigureAwait(false);
            await PromptAsync(next.Content).ConfigureAwait(false);
        }
    }

    public Task CancelAsync() => _agent.CancelAsync(_cts.Token);

    public Task SetModeAsync(string modeId) => _agent.SetModeAsync(modeId, _cts.Token);

    public IReadOnlyList<Agnes.Abstractions.SessionMode> Modes => _agent.Modes;
    public string? CurrentModeId => _agent.CurrentModeId;

    public Task RespondToPermissionAsync(string requestId, string optionId)
    {
        // A host-originated request (credential broker) is resolved locally; anything else is the
        // agent's own permission request and goes back to the agent.
        if (_hostPermissions.TryGetValue(requestId, out var pending))
        {
            pending.TrySetResult(string.Equals(optionId, "allow", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        return _agent.RespondToPermissionAsync(requestId, optionId, _cts.Token);
    }

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<QuestionAnswer> answers)
        => _agent.AnswerQuestionAsync(requestId, answers, _cts.Token);

    /// <summary>
    /// Surfaces a permission card for a brokered git push and waits for the user's answer (times out to
    /// a deny so a never-answered push doesn't hang the broker forever). Returns true iff allowed.
    /// </summary>
    public async Task<bool> RequestGitPermissionAsync(string host, string? repo)
    {
        var requestId = "gitcred-" + Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostPermissions[requestId] = tcs;

        var target = string.IsNullOrEmpty(repo) ? host : repo;
        var options = new[]
        {
            new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
            new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce),
        };
        await AppendAndPublishAsync(new PermissionRequestedEvent(requestId, string.Empty,
            $"Allow the sandboxed agent to use your GitHub account for {target}? (clone, fetch and push — asked once for this repository)", options)).ConfigureAwait(false);

        bool allowed;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _cts.Token);
            await using var registration = linked.Token.Register(() => tcs.TrySetResult(false));
            allowed = await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _hostPermissions.TryRemove(requestId, out _);
        }

        await AppendAndPublishAsync(new PermissionResolvedEvent(requestId, allowed ? "allow" : "deny",
            allowed ? PermissionOutcome.Allowed : PermissionOutcome.Denied)).ConfigureAwait(false);
        return allowed;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var @event in _agent.Events.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (@event is SessionStartedEvent started && !string.IsNullOrEmpty(started.AgentSessionId))
                {
                    AgentSessionStarted?.Invoke(started.AgentSessionId);
                }

                await AppendAndPublishAsync(@event).ConfigureAwait(false);

                if (@event is TurnEndedEvent)
                {
                    TurnCompleted?.Invoke();
                    await OnTurnEndedAsync().ConfigureAwait(false);
                }
                else if (@event is AgentErrorEvent error)
                {
                    AgentError?.Invoke(error.Message);
                }
            }

            // The agent's stream ended on its own. If we didn't ask it to stop (dispose cancels _cts),
            // the CLI process died — signal a fault so the host can restart + resume it.
            SignalFaultIfUnexpected();
        }
        catch (OperationCanceledException)
        {
            // Session disposed — an intentional stop, not a fault.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event pump failed for session {SessionId}", SessionId);
            SignalFaultIfUnexpected();
        }
    }

    private void SignalFaultIfUnexpected()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        // Fire the callback at most once, off the pump so recovery can dispose this session freely.
        if (Interlocked.Exchange(ref _faultSignalled, 1) == 0 && Faulted is { } faulted)
        {
            _logger.LogWarning("Agent stream ended unexpectedly for session {SessionId}; signalling fault", SessionId);
            faulted();
        }
    }

    private async Task AppendAndPublishAsync(SessionEvent @event)
    {
        // Redaction hook: a plugin may suppress this event from reaching clients (still logged).
        var gate = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforeAgentEventEvent(SessionId, @event)).ConfigureAwait(false);

        var stored = await _store.AppendAsync(SessionId, @event, _cts.Token).ConfigureAwait(false);
        if (!gate.IsCanceled)
        {
            await _broadcaster.PublishAsync(SessionId, stored).ConfigureAwait(false);
        }

        // Every inbound agent event is dispatchable on the spine with full typing (SessionEvent : IAgnesEvent),
        // so a plugin can observe ToolCallEvent, TurnEndedEvent, etc. directly.
        await _bus.DispatchAsync(stored).ConfigureAwait(false);
    }

    /// <summary>Records a forwarded MCP tool call in the session log (audit; from the forward proxy).</summary>
    public Task RecordMcpCallAsync(string server, string tool)
        => AppendAndPublishAsync(new McpToolCallEvent(server, tool));

    /// <summary>Records a brokered git-credential grant/denial in the session log (audit).</summary>
    public Task RecordGitCredentialAsync(string host, string? repo, bool allowed)
        => AppendAndPublishAsync(new GitCredentialEvent(host, repo, allowed));

    public async ValueTask DisposeAsync()
    {
        // Session teardown: any still-queued message can no longer be delivered, so move it to the discarded
        // list (visible, not silently dropped) BEFORE cancelling _cts — the append uses that token.
        await DiscardQueuedAsync().ConfigureAwait(false);

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pump completed with error during dispose");
        }

        await _agent.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
