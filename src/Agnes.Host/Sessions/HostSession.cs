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

    // Host-originated permission requests (e.g. the credential broker asking to push) awaiting a
    // client's answer — kept apart from agent-originated ones, which are answered by the agent.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _hostPermissions = new();

    public HostSession(
        string sessionId,
        string adapterId,
        string workingDirectory,
        IAgentSession agent,
        IEventStore store,
        ISessionBroadcaster broadcaster,
        ILogger logger)
    {
        SessionId = sessionId;
        AdapterId = adapterId;
        WorkingDirectory = workingDirectory;
        _agent = agent;
        _store = store;
        _broadcaster = broadcaster;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    public string SessionId { get; }
    public string AdapterId { get; }
    public string WorkingDirectory { get; }

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

    /// <summary>
    /// Surfaces a permission card for a brokered git push and waits for the user's answer (times out to
    /// a deny so a never-answered push doesn't hang the broker forever). Returns true iff allowed.
    /// </summary>
    public async Task<bool> RequestGitPermissionAsync(string host, string? repo)
    {
        var requestId = "gitcred-" + Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostPermissions[requestId] = tcs;

        var target = string.IsNullOrEmpty(repo) ? host : $"{repo}";
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
                await AppendAndPublishAsync(@event).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event pump failed for session {SessionId}", SessionId);
        }
    }

    private async Task AppendAndPublishAsync(SessionEvent @event)
    {
        var stored = await _store.AppendAsync(SessionId, @event, _cts.Token).ConfigureAwait(false);
        await _broadcaster.PublishAsync(SessionId, stored).ConfigureAwait(false);
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
