using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Hosting;
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
    private readonly McpRegistry? _mcp;
    private readonly McpForwardRegistry? _forward;
    private readonly McpForwardListener? _forwardListener;
    private readonly SandboxImageManager? _images;
    private readonly Projects.ProjectStore? _projects;
    private readonly ConcurrentDictionary<string, Projects.Project> _projectBySession = new();
    private readonly CredentialBrokerRegistry? _credentialBroker;
    private readonly CredentialBrokerListener? _credentialListener;
    private readonly ConcurrentDictionary<string, string> _forwardTokenBySession = new();
    private readonly ConcurrentDictionary<string, string> _credentialTokenBySession = new();
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
        ClaudeTokenRotationPusher? rotationPusher = null,
        McpRegistry? mcp = null,
        McpForwardRegistry? forward = null,
        McpForwardListener? forwardListener = null,
        SandboxImageManager? images = null,
        CredentialBrokerRegistry? credentialBroker = null,
        CredentialBrokerListener? credentialListener = null,
        Projects.ProjectStore? projects = null)
    {
        _adapters = adapters.ToDictionary(a => a.Descriptor.Id);
        _store = store;
        _broadcaster = broadcaster;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionManager>();
        _sandboxes = sandboxes;
        _credentialProviders = credentialProviders?.ToArray() ?? [];
        _rotationPusher = rotationPusher;
        _mcp = mcp;
        _forward = forward;
        _forwardListener = forwardListener;
        _images = images;
        _projects = projects;
        _credentialBroker = credentialBroker;
        _credentialListener = credentialListener;
        if (_forwardListener is not null)
        {
            _forwardListener.OnToolCall = OnForwardedToolCall;
        }

        if (_credentialListener is not null)
        {
            _credentialListener.OnAuthorize = OnGitCredentialAuthorizeAsync;
            _credentialListener.OnUse = OnGitCredentialUsed;
        }
    }

    // The broker asks whether a sandboxed push may proceed: "Trust" grants auto-allow; "Ask" surfaces
    // a permission card in the session and waits for the user.
    private async Task<bool> OnGitCredentialAuthorizeAsync(CredentialGrant grant, CredentialRequest request)
    {
        if (grant.SessionId is null || !_sessions.TryGetValue(grant.SessionId, out var session))
        {
            return false;
        }

        return string.Equals(grant.Mode, "Trust", StringComparison.OrdinalIgnoreCase)
            || await session.RequestGitPermissionAsync(request.Host, request.Repo).ConfigureAwait(false);
    }

    // A sandboxed agent obtained (or was denied) a brokered credential — record it in the session log.
    private void OnGitCredentialUsed(string token, CredentialRequest request, bool allowed)
    {
        if (_credentialBroker?.SessionFor(token) is { } sessionId && _sessions.TryGetValue(sessionId, out var session))
        {
            _ = session.RecordGitCredentialAsync(request.Host, request.Repo, allowed);
        }
    }

    // A sandboxed agent called a forwarded host MCP tool — record it in that session's log (audit).
    private void OnForwardedToolCall(string token, string server, string tool)
    {
        if (_forward?.SessionFor(token) is { } sessionId && _sessions.TryGetValue(sessionId, out var session))
        {
            _ = session.RecordMcpCallAsync(server, tool);
        }
    }

    public IReadOnlyList<AgentInfo> ListAgents()
        => _adapters.Values
            // When agents run in a sandbox, availability reflects the baked image (host PATH is
            // irrelevant — the agent runs in the VM, not on the host).
            .Select(a => new AgentInfo(a.Descriptor.Id, a.Descriptor.DisplayName, a.Descriptor.Version,
                Available: _images is not null
                    ? (_projects is not null ? _images.ImageHasAgent(_projects.Default(), a.Descriptor.Id) : _images.ImageHasAgent(a.Descriptor.Id))
                    : a.IsAvailable()))
            .ToArray();

    public async Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", CancellationToken cancellationToken = default)
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

        // Resolve this session's project from the working directory's repo (auto-created + editable);
        // the sandbox / MCP / credential steps below use the project's own config.
        Projects.Project? project = null;
        if (_projects is not null)
        {
            var remote = await _git.GetRemoteUrlAsync(effectiveDirectory, cancellationToken).ConfigureAwait(false);
            var repoKey = GitRemote.TryParse(remote, out var remoteHost, out var remoteRepo) ? $"{remoteHost}/{remoteRepo}" : string.Empty;
            project = _projects.Resolve(repoKey);
            _projectBySession[sessionId] = project;
            _logger.LogInformation("Session {SessionId} uses project '{Project}' ({Scope}).",
                sessionId, project.Name, repoKey.Length == 0 ? "default" : repoKey);
        }

        // Optionally provision a sandbox and run the agent inside it. Credentials and MCP config
        // (RunAt=Sandbox servers, plus RunAt=Host servers wired to the forward shim) are materialized
        // together in ONE bundle so the combined env write doesn't clobber the credential env file.
        ISandbox? sandbox = null;
        string? mcpConfigPath = null;
        if (_sandboxes is not null)
        {
            // Ensure the image exists (bake if missing) before launching from it — the resolved
            // project's own sandbox image when we have a project, else the legacy global baseline.
            var image = string.Empty;
            if (_images is not null)
            {
                if (project is not null)
                {
                    image = await _images.EnsureForProjectAsync(project, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _images.EnsureAsync(cancellationToken).ConfigureAwait(false);
                    image = _images.Alias;
                }
            }

            sandbox = await _sandboxes.CreateAsync(
                new SandboxSpec { HostWorkingDirectory = effectiveDirectory, ImageReference = image }, cancellationToken).ConfigureAwait(false);
            _sandboxBySession[sessionId] = sandbox;

            var env = new Dictionary<string, string>();
            var files = new List<SandboxCredentialFile>();

            var credentialProvider = _credentialProviders.FirstOrDefault(p => p.Handles(adapterId));
            if (credentialProvider is not null)
            {
                var credential = await credentialProvider.GetAsync(adapterId, cancellationToken).ConfigureAwait(false);
                foreach (var (k, v) in credential.EnvironmentVariables)
                {
                    env[k] = v;
                }

                files.AddRange(credential.Files);
            }

            mcpConfigPath = AddSandboxMcp(adapterId, sandbox, sessionId, skipPermissions, mcpApproval, env, files);
            await AddSandboxGitCredentialsAsync(sandbox, sessionId, effectiveDirectory, gitCredentialMode, env, files, cancellationToken).ConfigureAwait(false);

            if (env.Count > 0 || files.Count > 0)
            {
                await sandbox.MaterializeCredentialAsync(
                    new SandboxCredential { EnvironmentVariables = env, Files = files }, cancellationToken).ConfigureAwait(false);
            }

            if (credentialProvider is not null)
            {
                _rotationPusher?.RegisterActiveSandbox(sandbox);
            }

            _logger.LogInformation("Session {SessionId} runs in sandbox {SandboxId}", sessionId, sandbox.Id);
        }
        else
        {
            mcpConfigPath = await MaterializeHostMcpAsync(adapterId, cancellationToken).ConfigureAwait(false);
        }

        var agent = await adapter.StartSessionAsync(
            new AgentSessionOptions
            {
                WorkingDirectory = sandbox is null ? effectiveDirectory : "/work",
                Sandbox = sandbox,
                SkipPermissions = skipPermissions,
                McpConfigPath = mcpConfigPath,
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
        return new SessionInfo(sessionId, adapterId, effectiveDirectory, head, agent.Modes, agent.CurrentModeId, MapSandbox(sandbox), skipPermissions);
    }

    // Which agents Agnes can inject an MCP config into, and how: (config format, home-relative file,
    // whether the CLI loads it via a flag). ACP bridges (claude-code, opencode) and host-side Codex
    // are deferred — see the plan.
    private static (string Format, string HomeRel, bool UsesFlag)? McpTargetFor(string adapterId) => adapterId switch
    {
        "claude-code-native" => ("claude", ".agnes/mcp.json", true),
        "codex" => ("codex", ".codex/config.toml", false),
        _ => null,
    };

    /// <summary>
    /// Builds a sandboxed session's MCP config into the given bundle (env + files): RunAt=Sandbox
    /// servers run in-VM directly; RunAt=Host servers are rewritten to launch the forward shim (which
    /// reaches the real host server through the proxy). Returns the config path for the CLI flag, or null.
    /// </summary>
    private string? AddSandboxMcp(string adapterId, ISandbox sandbox, string sessionId,
        bool skipPermissions, string mcpApproval, Dictionary<string, string> env, List<SandboxCredentialFile> files)
    {
        if (_mcp is null || McpTargetFor(adapterId) is not { } target)
        {
            return null;
        }

        var entries = new List<McpServerInfo>(_mcp.Applicable(McpRunAt.Sandbox));

        // An autonomous session doesn't prompt per tool, so host servers are only forwarded to it
        // when the user has chosen to trust them (the "Ask vs Trust" preference). Attended sessions
        // always get forwarding — the agent's own permission protocol gates each tool call.
        var forwardAllowed = !skipPermissions || string.Equals(mcpApproval, "Trust", StringComparison.OrdinalIgnoreCase);
        var hostServers = _forward is not null && _forwardListener is not null && forwardAllowed
            ? _mcp.Applicable(McpRunAt.Host)
            : [];
        if (hostServers.Count > 0)
        {
            var shimVmPath = $"{sandbox.HomeDirectory.TrimEnd('/')}/{McpForward.ShimHomeRelativePath}";
            files.Add(new SandboxCredentialFile(McpForward.ShimHomeRelativePath, McpForward.ShimScript));

            var token = _forward!.Register(hostServers, sessionId);
            _forwardTokenBySession[sessionId] = token;
            env["AGNES_MCP_HOST"] = _forwardListener!.AdvertiseHost;
            env["AGNES_MCP_PORT"] = _forwardListener.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            env["AGNES_MCP_TOKEN"] = token;

            entries.AddRange(hostServers.Select(s => ShimEntry(s, shimVmPath)));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var content = target.Format == "claude" ? McpConfig.ForClaude(entries) : McpConfig.ForCodex(entries);
        files.Add(new SandboxCredentialFile(target.HomeRel, content));
        _logger.LogInformation("Materialized {Count} MCP server(s) into sandbox {SandboxId}", entries.Count, sandbox.Id);
        return target.UsesFlag ? $"{sandbox.HomeDirectory.TrimEnd('/')}/{target.HomeRel}" : null;
    }

    // A RunAt=Host server, as the sandbox sees it: launch the forward shim, which tunnels to the host.
    private static McpServerInfo ShimEntry(McpServerInfo s, string shimVmPath) => new(
        s.Id, s.Name, s.RunAt, s.Enabled, "stdio", "python3", [shimVmPath, s.Name],
        new Dictionary<string, string>(), null, null);

    /// <summary>
    /// Wires git credential brokering into a sandboxed session's bundle: derives the push scope from
    /// the working directory's origin remote, grants a per-session token, and materializes the guest
    /// git helper + ~/.gitconfig (helper + useHttpPath + commit identity) + the AGNES_GIT_* env. The
    /// agent can then <c>git push</c>; the broker mints a scoped credential on the host at push time.
    /// </summary>
    private async Task AddSandboxGitCredentialsAsync(ISandbox sandbox, string sessionId, string hostWorkingDirectory,
        string gitCredentialMode, Dictionary<string, string> env, List<SandboxCredentialFile> files, CancellationToken cancellationToken)
    {
        if (_credentialBroker is null || _credentialListener is null
            || string.IsNullOrWhiteSpace(gitCredentialMode) || string.Equals(gitCredentialMode, "Off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var remote = await _git.GetRemoteUrlAsync(hostWorkingDirectory, cancellationToken).ConfigureAwait(false);
        if (!GitRemote.TryParse(remote, out var host, out var repo))
        {
            _logger.LogInformation("Session {SessionId}: git credentials requested but no parseable origin remote; skipping.", sessionId);
            return;
        }

        var (userName, userEmail) = await _git.GetIdentityAsync(hostWorkingDirectory, cancellationToken).ConfigureAwait(false);

        var mode = string.Equals(gitCredentialMode, "Trust", StringComparison.OrdinalIgnoreCase) ? "Trust" : "Ask";
        var token = _credentialBroker.Register(new CredentialGrant(sessionId, host, repo, mode));
        _credentialTokenBySession[sessionId] = token;

        env["AGNES_GIT_HOST"] = _credentialListener.AdvertiseHost;
        env["AGNES_GIT_PORT"] = _credentialListener.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["AGNES_GIT_TOKEN"] = token;

        files.Add(new SandboxCredentialFile(GitCredentialHelper.HelperHomeRelativePath, GitCredentialHelper.Script));
        files.Add(new SandboxCredentialFile(".gitconfig", GitCredentialHelper.GitConfig(sandbox.HomeDirectory, userName, userEmail)));
        _logger.LogInformation("Session {SessionId}: git push to {Repo} on {Host} brokered ({Mode}).", sessionId, repo, host, mode);
    }

    /// <summary>Writes a host (non-sandbox) session's RunAt=Host MCP config to a temp file for the CLI flag.</summary>
    private async Task<string?> MaterializeHostMcpAsync(string adapterId, CancellationToken cancellationToken)
    {
        if (_mcp is null || McpTargetFor(adapterId) is not { UsesFlag: true })
        {
            return null; // only the config-flag (Claude) host path is wired; host-Codex/ACP deferred
        }

        var servers = _mcp.Applicable(McpRunAt.Host);
        if (servers.Count == 0)
        {
            return null;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"agnes-mcp-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(tempFile, McpConfig.ForClaude(servers), cancellationToken).ConfigureAwait(false);
        return tempFile;
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
        if (_forwardTokenBySession.TryRemove(sessionId, out var token))
        {
            _forward?.Unregister(token);
        }

        if (_credentialTokenBySession.TryRemove(sessionId, out var credentialToken))
        {
            _credentialBroker?.Unregister(credentialToken);
        }

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
        var skipPermissions = _catalog.TryGetValue(sessionId, out var rec) && rec.SkipPermissions;
        var info = new SessionInfo(sessionId, adapterId, workingDirectory, head,
            live?.Modes, live?.CurrentModeId, GetSandboxStatus(sessionId), skipPermissions);
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
