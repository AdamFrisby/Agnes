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
        => _agent.RespondToPermissionAsync(requestId, optionId, _cts.Token);

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
