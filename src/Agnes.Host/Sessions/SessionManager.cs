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
    private readonly SandboxRegistry? _sandboxRegistry;
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
        Projects.ProjectStore? projects = null,
        SandboxRegistry? sandboxRegistry = null)
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
        _sandboxRegistry = sandboxRegistry;
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
    // Ask-once-per-repository consent (prompts once per repo, remembers the decision for the session).
    private readonly GitConsentCache _gitConsent = new();

    /// <summary>
    /// If the project declares a checkout repo and the working directory is empty, clone it on the host
    /// (with a short-lived minted token, scrubbed from .git/config afterwards) before the agent launches,
    /// so the session starts on a ready checkout. Returns the (possibly unchanged) working directory.
    /// </summary>
    private async Task EnsureProjectCheckoutAsync(Projects.Project? project, string workingDirectory, string sessionId, CancellationToken cancellationToken)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Repo) || _credentialListener is null)
        {
            return;
        }

        if (Git.GitService.IsGitRepo(workingDirectory))
        {
            return; // already a checkout — never clobber existing work.
        }

        if (!Git.GitService.IsEmptyOrMissing(workingDirectory))
        {
            _logger.LogInformation("Session {SessionId}: working dir isn't empty and isn't a repo; skipping auto-checkout.", sessionId);
            return;
        }

        if (!TryParseRepoSpec(project.Repo!, out var host, out var repo, out var cleanUrl))
        {
            _logger.LogWarning("Session {SessionId}: project '{Project}' has an unparseable checkout repo '{Repo}'.", sessionId, project.Name, project.Repo);
            return;
        }

        var cred = await _credentialListener.MintAsync(new CredentialRequest("https", host, repo, "get"), cancellationToken).ConfigureAwait(false);
        if (cred is null)
        {
            _logger.LogWarning("Session {SessionId}: no linked account can check out {Repo}; leaving the working dir empty.", sessionId, repo);
            return;
        }

        var authUrl = $"https://{Uri.EscapeDataString(cred.Username)}:{Uri.EscapeDataString(cred.Password)}@{host}/{repo}.git";
        var (ok, message) = await _git.CloneAsync(cleanUrl, authUrl, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            _logger.LogInformation("Session {SessionId}: checked out {Repo} into the working directory.", sessionId, repo);
        }
        else
        {
            _logger.LogWarning("Session {SessionId}: checkout of {Repo} failed: {Message}", sessionId, repo, message);
        }
    }

    /// <summary>A human label for a sandbox in the list — the project name, else the working-dir folder.</summary>
    private static string SandboxTitle(Projects.Project? project, string workingDirectory)
    {
        if (project is { IsDefault: false, Name.Length: > 0 })
        {
            return project.Name;
        }

        var name = Path.GetFileName(workingDirectory.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? "session" : name;
    }

    /// <summary>Parses a checkout spec ("owner/repo", an https URL, or an ssh URL) into host + "owner/repo" + a clean https clone URL.</summary>
    internal static bool TryParseRepoSpec(string spec, out string host, out string repo, out string cleanUrl)
    {
        host = "github.com";
        repo = string.Empty;
        cleanUrl = string.Empty;
        spec = spec.Trim();

        if (spec.Contains("://", StringComparison.Ordinal) || spec.Contains('@'))
        {
            if (!GitRemote.TryParse(spec, out host, out repo))
            {
                return false;
            }
        }
        else
        {
            var parts = spec.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            repo = $"{parts[0]}/{parts[1]}";
        }

        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        cleanUrl = $"https://{host}/{repo}.git";
        return repo.Contains('/', StringComparison.Ordinal);
    }

    private async Task<bool> OnGitCredentialAuthorizeAsync(CredentialGrant grant, CredentialRequest request)
    {
        if (grant.SessionId is null || !_sessions.TryGetValue(grant.SessionId, out var session))
        {
            return false;
        }

        return await _gitConsent.DecideAsync(grant.SessionId, request.Host, request.Repo, grant.Mode,
            () => session.RequestGitPermissionAsync(request.Host, request.Repo)).ConfigureAwait(false);
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

        // Auto-checkout: if the project declares a repo and the working dir is empty, clone it (host-side,
        // scrubbed) before anything launches — so the agent starts on a ready checkout.
        await EnsureProjectCheckoutAsync(project, effectiveDirectory, sessionId, cancellationToken).ConfigureAwait(false);

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

            // Record the sandbox so it stays visible/manageable (resume/delete) even after a restart.
            var now = DateTimeOffset.UtcNow;
            _sandboxRegistry?.Upsert(new SandboxRecord(
                sessionId, sandbox.Id, sandbox.Info.Provider, adapterId, effectiveDirectory,
                project?.Name, SandboxTitle(project, effectiveDirectory), "running", now, now,
                skipPermissions, mcpApproval, gitCredentialMode));

            mcpConfigPath = await ProvisionSandboxContentsAsync(
                sandbox, sessionId, adapterId, effectiveDirectory, project, skipPermissions, mcpApproval, gitCredentialMode, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Session {SessionId} runs in sandbox {SandboxId}", sessionId, sandbox.Id);
        }
        else
        {
            mcpConfigPath = await MaterializeHostMcpAsync(adapterId, project, cancellationToken).ConfigureAwait(false);
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
        return new SessionInfo(sessionId, adapterId, effectiveDirectory, head, agent.Modes, agent.CurrentModeId, MapSandbox(sandbox), skipPermissions, project?.Name);
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
    // The MCP servers applicable to a session at a given run-location — from its project when we have
    // one (so two projects can expose different servers), else the host's global registry (legacy).
    private IReadOnlyList<McpServerInfo> ApplicableMcp(Projects.Project? project, McpRunAt runAt)
    {
        if (project is not null)
        {
            var want = runAt == McpRunAt.Sandbox ? "sandbox" : "host";
            return project.McpServers.Where(s => s.Enabled && string.Equals(s.RunAt, want, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return _mcp?.Applicable(runAt) ?? [];
    }

    private string? AddSandboxMcp(string adapterId, ISandbox sandbox, string sessionId,
        bool skipPermissions, string mcpApproval, Projects.Project? project, Dictionary<string, string> env, List<SandboxCredentialFile> files)
    {
        if (McpTargetFor(adapterId) is not { } target)
        {
            return null;
        }

        var entries = new List<McpServerInfo>(ApplicableMcp(project, McpRunAt.Sandbox));

        // An autonomous session doesn't prompt per tool, so host servers are only forwarded to it
        // when the user has chosen to trust them (the "Ask vs Trust" preference). Attended sessions
        // always get forwarding — the agent's own permission protocol gates each tool call.
        var forwardAllowed = !skipPermissions || string.Equals(mcpApproval, "Trust", StringComparison.OrdinalIgnoreCase);
        var hostServers = _forward is not null && _forwardListener is not null && forwardAllowed
            ? ApplicableMcp(project, McpRunAt.Host)
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
            return; // explicit opt-out: no credential helper in the sandbox at all.
        }

        // Which host to broker for: the working dir's origin if it has one, else GitHub (so the helper
        // is present even before anything is cloned — e.g. the agent clones a private repo into an empty
        // working dir). We deliberately DON'T scope the grant to the origin repo: git asks for a
        // credential the same way for clone/fetch/push and for any repo, so the grant covers the whole
        // host ("*") and each distinct repo is gated once by the ask-once-per-repo consent below.
        var remote = await _git.GetRemoteUrlAsync(hostWorkingDirectory, cancellationToken).ConfigureAwait(false);
        var host = GitRemote.TryParse(remote, out var remoteHost, out _) ? remoteHost : "github.com";

        // Nothing to broker if no account is linked for this host — skip the helper (the first-run link
        // prompt handles onboarding) rather than install a helper that can only ever fail.
        if (!_credentialListener.HasSourceFor(host))
        {
            _logger.LogInformation("Session {SessionId}: no linked credential source for {Host}; git helper not installed.", sessionId, host);
            return;
        }

        var (userName, userEmail) = await _git.GetIdentityAsync(hostWorkingDirectory, cancellationToken).ConfigureAwait(false);

        var mode = string.Equals(gitCredentialMode, "Trust", StringComparison.OrdinalIgnoreCase) ? "Trust" : "Ask";
        var token = _credentialBroker.Register(new CredentialGrant(sessionId, host, "*", mode));
        _credentialTokenBySession[sessionId] = token;

        env["AGNES_GIT_HOST"] = _credentialListener.AdvertiseHost;
        env["AGNES_GIT_PORT"] = _credentialListener.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["AGNES_GIT_TOKEN"] = token;

        files.Add(new SandboxCredentialFile(GitCredentialHelper.HelperHomeRelativePath, GitCredentialHelper.Script));
        files.Add(new SandboxCredentialFile(".gitconfig", GitCredentialHelper.GitConfig(sandbox.HomeDirectory, userName, userEmail)));
        _logger.LogInformation("Session {SessionId}: GitHub access on {Host} brokered ({Mode}, per-repo consent).", sessionId, host, mode);
    }

    /// <summary>Writes a host (non-sandbox) session's RunAt=Host MCP config to a temp file for the CLI flag.</summary>
    private async Task<string?> MaterializeHostMcpAsync(string adapterId, Projects.Project? project, CancellationToken cancellationToken)
    {
        if (McpTargetFor(adapterId) is not { UsesFlag: true })
        {
            return null; // only the config-flag (Claude) host path is wired; host-Codex/ACP deferred
        }

        var servers = ApplicableMcp(project, McpRunAt.Host);
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

        _gitConsent.Forget(sessionId); // drop this session's per-repo git consents.

        if (_sandboxBySession.TryRemove(sessionId, out var sandbox))
        {
            await sandbox.DeleteAsync().ConfigureAwait(false);
        }
        else if (_sandboxes is not null && _sandboxRegistry?.Get(sessionId) is { } record)
        {
            // No in-memory handle (e.g. a sandbox from a prior daemon run) — reconnect by name and destroy it.
            var attached = await _sandboxes.AttachAsync(record.VmName, new SandboxSpec(), start: false).ConfigureAwait(false);
            await attached.DeleteAsync().ConfigureAwait(false);
        }

        _sandboxRegistry?.Remove(sessionId);
    }

    /// <summary>
    /// Explicit stop-on-close: end the running agent and shut the sandbox VM down (freeing CPU + RAM),
    /// but KEEP the VM and the session record so it can be resumed later. Non-destructive — only
    /// <see cref="DeleteSandboxAsync"/> actually destroys the sandbox.
    /// </summary>
    public async Task StopSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (_sandboxBySession.TryGetValue(sessionId, out var sandbox) && sandbox is IStoppableSandbox stoppable)
        {
            await stoppable.StopAsync().ConfigureAwait(false);
            _sandboxRegistry?.SetState(sessionId, "stopped", DateTimeOffset.UtcNow);
            _logger.LogInformation("Session {SessionId}: sandbox shut down on close (kept for resume).", sessionId);
        }
    }

    /// <summary>Materializes a sandbox's credentials + MCP config + git-credential wiring (env + files)
    /// and pushes them in, returning the MCP config path. Shared by open and resume — on resume the VM's
    /// tmpfs was lost when it stopped, so everything is re-materialized.</summary>
    private async Task<string?> ProvisionSandboxContentsAsync(
        ISandbox sandbox, string sessionId, string adapterId, string effectiveDirectory, Projects.Project? project,
        bool skipPermissions, string mcpApproval, string gitCredentialMode, CancellationToken cancellationToken)
    {
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

        var mcpConfigPath = AddSandboxMcp(adapterId, sandbox, sessionId, skipPermissions, mcpApproval, project, env, files);
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

        return mcpConfigPath;
    }

    /// <summary>
    /// Resume a stopped/closed sandboxed session: reconnect to its VM (by name — works even after a host
    /// restart), cold-start it, re-materialize the agent's credentials/MCP, and re-launch the agent under
    /// the SAME session id (so its transcript continues). Returns the reopened session's info.
    /// </summary>
    public async Task<SessionInfo> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var already))
        {
            // Already live — nothing to resume.
            var liveHead = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new SessionInfo(sessionId, already.AdapterId, "/work", liveHead, already.Modes, already.CurrentModeId,
                _sandboxBySession.TryGetValue(sessionId, out var s) ? MapSandbox(s) : null, false, null);
        }

        var record = _sandboxRegistry?.Get(sessionId)
            ?? throw new InvalidOperationException($"No sandbox recorded for session '{sessionId}'.");
        if (_sandboxes is null)
        {
            throw new InvalidOperationException("Sandboxing is not configured on this host.");
        }

        if (!_adapters.TryGetValue(record.AdapterId, out var adapter))
        {
            throw new InvalidOperationException($"Adapter '{record.AdapterId}' is no longer registered.");
        }

        var effectiveDirectory = record.WorkingDirectory;
        var project = await ResolveProjectForAsync(sessionId, effectiveDirectory, cancellationToken).ConfigureAwait(false);

        // Reconnect to the existing VM (this run's handle if we still have it, else by name) and start it.
        var sandbox = await _sandboxes.AttachAsync(
            record.VmName, new SandboxSpec { HostWorkingDirectory = effectiveDirectory }, start: true, cancellationToken).ConfigureAwait(false);
        _sandboxBySession[sessionId] = sandbox;

        var mcpConfigPath = await ProvisionSandboxContentsAsync(
            sandbox, sessionId, record.AdapterId, effectiveDirectory, project,
            record.SkipPermissions, record.McpApproval, record.GitCredentialMode, cancellationToken).ConfigureAwait(false);

        var agent = await adapter.StartSessionAsync(
            new AgentSessionOptions
            {
                WorkingDirectory = "/work",
                Sandbox = sandbox,
                SkipPermissions = record.SkipPermissions,
                McpConfigPath = mcpConfigPath,
            },
            cancellationToken).ConfigureAwait(false);

        var session = new HostSession(
            sessionId, record.AdapterId, effectiveDirectory, agent, _store, _broadcaster,
            _loggerFactory.CreateLogger<HostSession>());
        _sessions[sessionId] = session;
        _sandboxRegistry?.SetState(sessionId, "running", DateTimeOffset.UtcNow);
        _logger.LogInformation("Resumed session {SessionId} in sandbox {SandboxId}", sessionId, sandbox.Id);

        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionInfo(sessionId, record.AdapterId, "/work", head, agent.Modes, agent.CurrentModeId,
            MapSandbox(sandbox), record.SkipPermissions, project?.Name);
    }

    /// <summary>Re-resolves the project for a working directory (same rule as open).</summary>
    private async Task<Projects.Project?> ResolveProjectForAsync(string sessionId, string workingDirectory, CancellationToken cancellationToken)
    {
        if (_projects is null)
        {
            return null;
        }

        var remote = await _git.GetRemoteUrlAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var repoKey = GitRemote.TryParse(remote, out var host, out var repo) ? $"{host}/{repo}" : string.Empty;
        var project = _projects.Resolve(repoKey);
        _projectBySession[sessionId] = project;
        return project;
    }

    /// <summary>All sandboxes the host tracks (running + stopped, this run + prior runs), for the list.</summary>
    public IReadOnlyList<SandboxRecordDto> ListSandboxes()
        => _sandboxRegistry?.List().Select(r => new SandboxRecordDto(
               r.SessionId, r.VmName, r.Provider, r.AdapterId, r.WorkingDirectory, r.ProjectName, r.Title,
               r.State, r.CreatedAt, r.LastUsedAt, _sessions.ContainsKey(r.SessionId))).ToArray()
           ?? [];

    /// <summary>Agnes-owned VMs on the host that no tracked session references (orphans from prior runs or
    /// crashes). Never deleted automatically — surfaced for a manual reap.</summary>
    public async Task<IReadOnlyList<string>> ListOrphanVmNamesAsync(CancellationToken cancellationToken = default)
    {
        if (_sandboxes is null)
        {
            return [];
        }

        var tracked = _sandboxRegistry?.TrackedVmNames() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managed = await _sandboxes.ListManagedAsync(cancellationToken).ConfigureAwait(false);
        return managed
            .Where(m => m.Id.StartsWith("agnes-", StringComparison.OrdinalIgnoreCase) && !tracked.Contains(m.Id))
            .Select(m => m.Id)
            .ToArray();
    }

    /// <summary>Deletes the orphaned Agnes VMs (manual action). Returns how many were reaped.</summary>
    public async Task<int> ReapOrphanSandboxesAsync(CancellationToken cancellationToken = default)
    {
        if (_sandboxes is null)
        {
            return 0;
        }

        var orphans = await ListOrphanVmNamesAsync(cancellationToken).ConfigureAwait(false);
        var reaped = 0;
        foreach (var name in orphans)
        {
            try
            {
                var sandbox = await _sandboxes.AttachAsync(name, new SandboxSpec(), start: false, cancellationToken).ConfigureAwait(false);
                await sandbox.DeleteAsync(cancellationToken).ConfigureAwait(false);
                reaped++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reap orphan sandbox {Name}", name);
            }
        }

        _logger.LogInformation("Reaped {Count} orphaned sandbox(es).", reaped);
        return reaped;
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
