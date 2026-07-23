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

    /// <summary>Records a user prompt in the log, then drives an agent turn in the background.</summary>
    public async Task PromptAsync(IReadOnlyList<ContentBlock> content)
    {
        foreach (var block in content)
        {
            await AppendAndPublishAsync(new MessageChunkEvent(MessageRole.User, block)).ConfigureAwait(false);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _agent.PromptAsync(content, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Prompt failed for session {SessionId}", SessionId);
                await AppendAndPublishAsync(new AgentErrorEvent(ex.Message)).ConfigureAwait(false);
            }
        });
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
