using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>Orchestrates agent adapters and live sessions, backed by the event store.</summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, IAgentAdapter> _adapters;
    private readonly IEventStore _store;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionManager> _logger;
    private readonly Git.GitService _git = new();
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new();

    public SessionManager(
        IEnumerable<IAgentAdapter> adapters,
        IEventStore store,
        ISessionBroadcaster broadcaster,
        ILoggerFactory loggerFactory)
    {
        _adapters = adapters.ToDictionary(a => a.Descriptor.Id);
        _store = store;
        _broadcaster = broadcaster;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionManager>();
    }

    public IReadOnlyList<AgentInfo> ListAgents()
        => _adapters.Values
            .Select(a => new AgentInfo(a.Descriptor.Id, a.Descriptor.DisplayName, a.Descriptor.Version, Available: true))
            .ToArray();

    public async Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (!_adapters.TryGetValue(adapterId, out var adapter))
        {
            throw new InvalidOperationException($"Unknown agent adapter '{adapterId}'.");
        }

        var agent = await adapter.StartSessionAsync(
            new AgentSessionOptions { WorkingDirectory = workingDirectory },
            cancellationToken).ConfigureAwait(false);

        var sessionId = Guid.NewGuid().ToString("n");
        var session = new HostSession(
            sessionId, adapterId, workingDirectory, agent, _store, _broadcaster,
            _loggerFactory.CreateLogger<HostSession>());
        _sessions[sessionId] = session;
        _logger.LogInformation("Opened session {SessionId} on {AdapterId}", sessionId, adapterId);

        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionInfo(sessionId, adapterId, workingDirectory, head, agent.Modes, agent.CurrentModeId);
    }

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
        => Require(sessionId).PromptAsync(content);

    public Task CancelAsync(string sessionId) => Require(sessionId).CancelAsync();

    public Task SetModeAsync(string sessionId, string modeId) => Require(sessionId).SetModeAsync(modeId);

    public Task<Agnes.Protocol.GitStatus> GetGitStatusAsync(string sessionId)
        => _git.GetStatusAsync(Require(sessionId).WorkingDirectory);

    public Task<Agnes.Protocol.GitCommitResult> GitCommitAsync(string sessionId, string message)
        => _git.CommitAsync(Require(sessionId).WorkingDirectory, message);

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
        => Require(sessionId).RespondToPermissionAsync(requestId, optionId);

    public async Task<SessionSnapshot> GetSnapshotAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        var session = Require(sessionId);
        var events = await _store.ReadSinceAsync(sessionId, sinceSequence, cancellationToken).ConfigureAwait(false);
        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var info = new SessionInfo(sessionId, session.AdapterId, session.WorkingDirectory, head, session.Modes, session.CurrentModeId);
        return new SessionSnapshot(info, events, head);
    }

    private HostSession Require(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new InvalidOperationException($"Unknown session '{sessionId}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
