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
            .Select(a => new AgentInfo(a.Descriptor.Id, a.Descriptor.DisplayName, a.Descriptor.Version, Available: true))
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

        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionInfo(sessionId, adapterId, effectiveDirectory, head, agent.Modes, agent.CurrentModeId, MapSandbox(sandbox));
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
        var info = new SessionInfo(sessionId, session.AdapterId, session.WorkingDirectory, head, session.Modes, session.CurrentModeId, GetSandboxStatus(sessionId));
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

        foreach (var (repo, worktree) in _worktrees.Values)
        {
            await _git.RemoveWorktreeAsync(repo, worktree).ConfigureAwait(false);
        }
    }
}
