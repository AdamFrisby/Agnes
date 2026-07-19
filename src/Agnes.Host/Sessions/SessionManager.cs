using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Protocol;
using Agnes.Sandbox;
using Agnes.Sandbox.Credentials;
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
    private readonly ISandboxProvider? _sandboxes;
    private readonly IReadOnlyList<IAgentCredentialProvider> _credentialProviders;
    private readonly ClaudeTokenRotationPusher? _rotationPusher;
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (string Repo, string Worktree)> _worktrees = new();
    private readonly ConcurrentDictionary<string, ISandbox> _sandboxBySession = new();
    private readonly ConcurrentDictionary<string, SessionRecord> _catalog = new();
    private readonly SemaphoreSlim _attachGate = new(1, 1);

    public SessionManager(
        IEnumerable<IAgentAdapter> adapters,
        IEventStore store,
        ISessionBroadcaster broadcaster,
        ILoggerFactory loggerFactory,
        ISandboxProvider? sandboxes = null,
        IEnumerable<IAgentCredentialProvider>? credentialProviders = null,
        ClaudeTokenRotationPusher? rotationPusher = null)
    {
        _adapters = adapters.ToDictionary(a => a.Descriptor.Id);
        _store = store;
        _broadcaster = broadcaster;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionManager>();
        _sandboxes = sandboxes;
        _credentialProviders = credentialProviders?.ToArray() ?? [];
        _rotationPusher = rotationPusher;
    }

    public IReadOnlyList<AgentInfo> ListAgents()
        => _adapters.Values
            .Select(a => new AgentInfo(a.Descriptor.Id, a.Descriptor.DisplayName, a.Descriptor.Version, Available: a.IsAvailable()))
            .ToArray();

    public async Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, CancellationToken cancellationToken = default)
    {
        if (!_adapters.TryGetValue(adapterId, out var adapter))
        {
            throw new InvalidOperationException($"Unknown agent adapter '{adapterId}'.");
        }

        var sessionId = Guid.NewGuid().ToString("n");
        var effectiveDirectory = workingDirectory;
        if (useWorktree)
        {
            var worktree = await _git.CreateWorktreeAsync(workingDirectory, sessionId, cancellationToken).ConfigureAwait(false);
            if (worktree is not null)
            {
                effectiveDirectory = worktree;
                _worktrees[sessionId] = (workingDirectory, worktree);
                _logger.LogInformation("Session {SessionId} isolated in worktree {Worktree}", sessionId, worktree);
            }
        }

        // Optionally provision a sandbox and run the agent inside it (with credentials materialised).
        ISandbox? sandbox = null;
        if (_sandboxes is not null)
        {
            sandbox = await _sandboxes.CreateAsync(
                new SandboxSpec { HostWorkingDirectory = effectiveDirectory }, cancellationToken).ConfigureAwait(false);
            _sandboxBySession[sessionId] = sandbox;

            var credentialProvider = _credentialProviders.FirstOrDefault(p => p.Handles(adapterId));
            if (credentialProvider is not null)
            {
                var credential = await credentialProvider.GetAsync(adapterId, cancellationToken).ConfigureAwait(false);
                await sandbox.MaterializeCredentialAsync(credential, cancellationToken).ConfigureAwait(false);
                _rotationPusher?.RegisterActiveSandbox(sandbox);
            }

            _logger.LogInformation("Session {SessionId} runs in sandbox {SandboxId}", sessionId, sandbox.Id);
        }

        var agent = await adapter.StartSessionAsync(
            new AgentSessionOptions
            {
                WorkingDirectory = sandbox is null ? effectiveDirectory : "/work",
                Sandbox = sandbox,
                SkipPermissions = skipPermissions,
            },
            cancellationToken).ConfigureAwait(false);

        var session = new HostSession(
            sessionId, adapterId, effectiveDirectory, agent, _store, _broadcaster,
            _loggerFactory.CreateLogger<HostSession>());
        _sessions[sessionId] = session;
        _logger.LogInformation("Opened session {SessionId} on {AdapterId}", sessionId, adapterId);

        // Catalogue the session so it survives a host restart (agent session id is updated as it runs).
        var record = new SessionRecord(
            sessionId, adapterId, effectiveDirectory, agent.AgentSessionId,
            useWorktree, skipPermissions, sandbox is not null, DateTimeOffset.UtcNow);
        _catalog[sessionId] = record;
        await _store.SaveSessionAsync(record, cancellationToken).ConfigureAwait(false);

        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionInfo(sessionId, adapterId, effectiveDirectory, head, agent.Modes, agent.CurrentModeId, MapSandbox(sandbox));
    }

    /// <summary>Loads the persisted session catalogue on startup. Sessions are dormant (history
    /// replays immediately); the agent re-attaches lazily on the first prompt.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var records = await _store.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            _catalog[record.SessionId] = record;
        }

        if (records.Count > 0)
        {
            _logger.LogInformation("Restored {Count} session(s) from the catalogue (dormant until prompted)", records.Count);
        }
    }

    /// <summary>Ensures a live agent is attached to a catalogued session, re-attaching after a restart.</summary>
    private async Task<HostSession> EnsureLiveAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var live))
        {
            return live;
        }

        if (!_catalog.TryGetValue(sessionId, out var record))
        {
            throw new InvalidOperationException($"Unknown session '{sessionId}'.");
        }

        await _attachGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(sessionId, out live))
            {
                return live;
            }

            if (!_adapters.TryGetValue(record.AdapterId, out var adapter))
            {
                throw new InvalidOperationException($"Adapter '{record.AdapterId}' for session '{sessionId}' is no longer registered.");
            }

            // Sandboxed sessions can't be re-attached across a restart yet (the VM handle is lost).
            if (record.Sandboxed)
            {
                var notice = await _store.AppendAsync(sessionId, new NoticeEvent(
                    "This sandboxed session can't be resumed after a host restart yet — fork it to continue in a new session.",
                    IsError: true)).ConfigureAwait(false);
                await _broadcaster.PublishAsync(sessionId, notice).ConfigureAwait(false);
                throw new InvalidOperationException("Sandboxed sessions cannot be resumed after a restart.");
            }

            _logger.LogInformation("Re-attaching agent for restored session {SessionId} (resume={Resume})", sessionId, record.AgentSessionId);
            var agent = await adapter.StartSessionAsync(
                new AgentSessionOptions
                {
                    WorkingDirectory = record.WorkingDirectory,
                    SkipPermissions = record.SkipPermissions,
                    ResumeSessionId = record.AgentSessionId,
                },
                CancellationToken.None).ConfigureAwait(false);

            var session = new HostSession(
                sessionId, record.AdapterId, record.WorkingDirectory, agent, _store, _broadcaster,
                _loggerFactory.CreateLogger<HostSession>());
            _sessions[sessionId] = session;
            var reconnected = await _store.AppendAsync(sessionId, new NoticeEvent(
                record.AgentSessionId is null ? "Reconnected with a fresh agent." : "Reconnected and resumed the agent.")).ConfigureAwait(false);
            await _broadcaster.PublishAsync(sessionId, reconnected).ConfigureAwait(false);
            return session;
        }
        finally
        {
            _attachGate.Release();
        }
    }

    // ---- sandbox lifecycle ----

    public async Task PauseSandboxAsync(string sessionId)
    {
        if (_sandboxBySession.TryGetValue(sessionId, out var sandbox) && sandbox is IPausableSandbox pausable)
        {
            await pausable.PauseAsync().ConfigureAwait(false);
        }
    }

    public async Task ResumeSandboxAsync(string sessionId)
    {
        if (_sandboxBySession.TryGetValue(sessionId, out var sandbox) && sandbox is IPausableSandbox pausable)
        {
            await pausable.ResumeAsync().ConfigureAwait(false);
        }
    }

    public async Task DeleteSandboxAsync(string sessionId)
    {
        if (_sandboxBySession.TryRemove(sessionId, out var sandbox))
        {
            await sandbox.DeleteAsync().ConfigureAwait(false);
        }
    }

    public SandboxStatus? GetSandboxStatus(string sessionId)
        => _sandboxBySession.TryGetValue(sessionId, out var sandbox) ? MapSandbox(sandbox) : null;

    private static SandboxStatus? MapSandbox(ISandbox? sandbox)
        => sandbox is null ? null : new SandboxStatus(sandbox.Info.Provider, sandbox.Info.Id, sandbox.Info.State.ToString());

    public async Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
    {
        var session = await EnsureLiveAsync(sessionId).ConfigureAwait(false);
        await session.PromptAsync(content).ConfigureAwait(false);
        await UpdateAgentSessionIdAsync(sessionId, session.AgentSessionId).ConfigureAwait(false);
    }

    // The native CLI's real session id arrives asynchronously (after the first turn's init line);
    // persist it once known so the agent can be resumed after a restart.
    private async Task UpdateAgentSessionIdAsync(string sessionId, string agentSessionId)
    {
        if (_catalog.TryGetValue(sessionId, out var record)
            && !string.IsNullOrEmpty(agentSessionId)
            && record.AgentSessionId != agentSessionId)
        {
            var updated = record with { AgentSessionId = agentSessionId };
            _catalog[sessionId] = updated;
            await _store.SaveSessionAsync(updated).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(string sessionId)
        => await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).CancelAsync().ConfigureAwait(false);

    public async Task SetModeAsync(string sessionId, string modeId)
        => await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).SetModeAsync(modeId).ConfigureAwait(false);

    public Task<Agnes.Protocol.GitStatus> GetGitStatusAsync(string sessionId)
        => _git.GetStatusAsync(WorkingDirectoryOf(sessionId));

    public Task<Agnes.Protocol.GitCommitResult> GitCommitAsync(string sessionId, string message)
        => _git.CommitAsync(WorkingDirectoryOf(sessionId), message);

    public async Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
        => await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).RespondToPermissionAsync(requestId, optionId).ConfigureAwait(false);

    public async Task<SessionSnapshot> GetSnapshotAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        // Served from the durable log — works for live and dormant (restored-but-not-yet-prompted) sessions.
        if (!_sessions.ContainsKey(sessionId) && !_catalog.ContainsKey(sessionId))
        {
            throw new InvalidOperationException($"Unknown session '{sessionId}'.");
        }

        var events = await _store.ReadSinceAsync(sessionId, sinceSequence, cancellationToken).ConfigureAwait(false);
        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var (adapterId, workingDirectory) = _sessions.TryGetValue(sessionId, out var live)
            ? (live.AdapterId, live.WorkingDirectory)
            : (_catalog[sessionId].AdapterId, _catalog[sessionId].WorkingDirectory);
        var info = new SessionInfo(sessionId, adapterId, workingDirectory, head,
            live?.Modes, live?.CurrentModeId, GetSandboxStatus(sessionId));
        return new SessionSnapshot(info, events, head);
    }

    private string WorkingDirectoryOf(string sessionId)
        => _sessions.TryGetValue(sessionId, out var live) ? live.WorkingDirectory
        : _catalog.TryGetValue(sessionId, out var record) ? record.WorkingDirectory
        : throw new InvalidOperationException($"Unknown session '{sessionId}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var (repo, worktree) in _worktrees.Values)
        {
            await _git.RemoveWorktreeAsync(repo, worktree).ConfigureAwait(false);
        }

        _attachGate.Dispose();
    }
}
