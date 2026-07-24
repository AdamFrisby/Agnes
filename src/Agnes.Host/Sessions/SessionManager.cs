using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Approvals;
using Agnes.Host.Files;
using Agnes.Host.Events;
using Agnes.Host.Sessions.Handoff;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Agnes.Sandbox;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>A lightweight per-session summary (see <see cref="SessionManager.ListSessionSummariesAsync"/>).</summary>
public sealed record SessionListEntry(
    string SessionId,
    string AdapterId,
    string? Title,
    string Status,
    long HeadSequence,
    string? CurrentModeId);

/// <summary>Orchestrates agent adapters and live sessions, backed by the event store.</summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly IPluginRegistry<IAgentAdapter> _adapters;
    private readonly IEventStore _store;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly Agnes.Abstractions.Events.IEventBus _bus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionManager> _logger;
    private readonly Git.GitService _git = new();
    // Shared one-shot-agent primitive (bounded, non-interactive single-turn runs) — reused for commit-message
    // generation and available for any other one-shot generation task.
    private readonly OneShotAgentRunner _oneShot = new();
    private readonly IReadOnlyList<IGitHostProvider> _gitHosts;
    private readonly ISandboxProvider? _sandboxes;
    private readonly SessionSecurityOptions _security;
    private readonly IReadOnlyList<IAgentCredentialProvider> _credentialProviders;
    private readonly ClaudeTokenRotationPusher? _rotationPusher;
    private readonly McpRegistry? _mcp;
    private readonly bool _mcpStrict;
    private readonly McpForwardRegistry? _forward;
    private readonly McpForwardListener? _forwardListener;
    private readonly SandboxImageManager? _images;
    private readonly Projects.ProjectStore? _projects;
    private readonly SandboxRegistry? _sandboxRegistry;
    private readonly CredentialBrokerRegistry? _credentialBroker;
    private readonly CredentialBrokerListener? _credentialListener;
    // External attention requests (extensibility/06) are unioned into the same approvals inbox; optional so
    // the many test/simulation constructions of SessionManager are unaffected (null ⇒ session permissions only).
    private readonly Attention.AttentionRequestStore? _attention;
    // Generic approval-gating (notifications/02 tier 2): consequential actions invoked from a gated surface
    // become durable ApprovalRequests unioned into the same inbox. Optional so the many test/simulation
    // constructions are unaffected (null ⇒ every action executes immediately, exactly as before this feature).
    private readonly ApprovalGateService? _approvals;
    // Host-level CLI-fallback terminal provider (platform/03). Optional so the many test/simulation
    // constructions are unaffected (null ⇒ terminals resolve only from a session whose agent is itself an
    // ICliFallback). Used both for in-session terminals and provider login — the ONE shared spawn path.
    private readonly ICliFallback? _cliFallback;
    // The prompt library, so enabled system-prompt additions are assembled and prepended to a session's
    // effective system prompt at open. Optional so the many test/simulation constructions are unaffected
    // (null ⇒ no additions).
    private readonly Hosting.PromptLibrary? _prompts;
    // Open fallback terminals, keyed by their (globally-unique) terminal id, so write/resize can reach the
    // live PTY handle. Distinct lifetime from a session's agent handle, hence its own map.
    private readonly ConcurrentDictionary<string, ITerminalHandle> _terminals = new();
    // Live handles keep their own maps — they have distinct lifetimes (a stopped session keeps its sandbox
    // for resume) and hotter concurrency than the metadata below.
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ISandbox> _sandboxBySession = new();
    private readonly ConcurrentDictionary<string, SessionRecord> _catalog = new();
    // All other per-session state, bundled so teardown is one operation (Forget) rather than wiping N
    // parallel dictionaries by hand — the leak seed that let a removed session linger in half its stores.
    private readonly ConcurrentDictionary<string, SessionState> _state = new();
    private readonly SemaphoreSlim _attachGate = new(1, 1);

    /// <summary>Mutable per-session metadata (brokered-credential tokens, resolved project, git worktree,
    /// recovery debounce, last title). Bundled into one entry per session id.</summary>
    private sealed class SessionState
    {
        public Projects.Project? Project;
        public string? ForwardToken;
        public string? CredentialToken;
        public (string Repo, string Worktree)? Worktree;
        public DateTimeOffset? LastRecoveryAt;
        public string? Title;

        // Read state: the highest sequence a client has viewed, plus a sticky "manually marked unread" flag
        // that keeps a session unread even while it's open until the next explicit mark-read.
        public long ReadCursor;
        public bool StickyUnread;

        // The send policy applied to a busy-send. Durable per session (survives an agent relaunch, which
        // rebuilds the live HostSession); re-applied to the live session on (re)attach.
        public SendPolicy SendPolicy = SendPolicy.QueueInAgent;

        // Direct/watch session: a read-only live tail of a CLI session Agnes did not start (sessions/02). Its
        // agent handle only tails an on-disk log — sending to it is rejected, and no crash-recovery is wired.
        public bool ReadOnly;
    }

    /// <summary>The session's metadata entry, created on first write.</summary>
    private SessionState State(string sessionId) => _state.GetOrAdd(sessionId, _ => new SessionState());

    /// <summary>The session's metadata entry if it exists, else null (a non-creating read).</summary>
    private SessionState? StateOrNull(string sessionId) => _state.TryGetValue(sessionId, out var s) ? s : null;

    /// <summary>Whether the session is a read-only Direct/watch of a CLI session Agnes did not start (sessions/02).</summary>
    private bool IsReadOnly(string sessionId) => StateOrNull(sessionId)?.ReadOnly ?? false;

    /// <summary>Idempotent teardown of one session's brokered wiring and metadata — the single place that
    /// forgets a session so no store is left holding a stale entry. Callers additionally handle the live
    /// handles (dispose the <see cref="HostSession"/>, stop/delete the sandbox) per their own semantics.</summary>
    private void Forget(string sessionId)
    {
        if (_state.TryRemove(sessionId, out var state))
        {
            if (state.ForwardToken is { } ft) { _forward?.Unregister(ft); }
            if (state.CredentialToken is { } ct) { _credentialBroker?.Unregister(ct); }
        }

        _gitConsent.Forget(sessionId); // drop this session's per-repo git consents.
    }

    /// <summary>If a just-restarted agent dies again within this window, stop auto-restarting and ask the
    /// user to restart manually — so a genuinely crash-looping agent doesn't thrash.</summary>
    private static readonly TimeSpan RecoveryDebounce = TimeSpan.FromSeconds(60);

    public SessionManager(
        IPluginRegistry<IAgentAdapter> adapters,
        IEventStore store,
        ISessionBroadcaster broadcaster,
        ILoggerFactory loggerFactory,
        IPluginRegistry<ISandboxProvider>? sandboxProviders = null,
        IEnumerable<IAgentCredentialProvider>? credentialProviders = null,
        ClaudeTokenRotationPusher? rotationPusher = null,
        McpRegistry? mcp = null,
        McpForwardRegistry? forward = null,
        McpForwardListener? forwardListener = null,
        SandboxImageManager? images = null,
        CredentialBrokerRegistry? credentialBroker = null,
        CredentialBrokerListener? credentialListener = null,
        Projects.ProjectStore? projects = null,
        SandboxRegistry? sandboxRegistry = null,
        Agnes.Abstractions.Events.IEventBus? eventBus = null,
        McpOptions? mcpOptions = null,
        IPluginRegistry<IGitHostProvider>? gitHosts = null,
        Attention.AttentionRequestStore? attention = null,
        ICliFallback? cliFallback = null,
        Hosting.PromptLibrary? promptLibrary = null,
        ApprovalGateService? approvals = null,
        SessionSecurityOptions? security = null)
    {
        _adapters = adapters;
        _gitHosts = gitHosts?.All.ToArray() ?? [];
        _store = store;
        _broadcaster = broadcaster;
        _loggerFactory = loggerFactory;
        _bus = eventBus ?? new Agnes.Abstractions.Events.EventBus();
        _logger = loggerFactory.CreateLogger<SessionManager>();
        _sandboxes = sandboxProviders?.All.FirstOrDefault();
        _security = security ?? new SessionSecurityOptions();
        _credentialProviders = credentialProviders?.ToArray() ?? [];
        _rotationPusher = rotationPusher;
        _mcp = mcp;
        _mcpStrict = mcpOptions?.Strict ?? false;
        _forward = forward;
        _forwardListener = forwardListener;
        _images = images;
        _projects = projects;
        _sandboxRegistry = sandboxRegistry;
        _credentialBroker = credentialBroker;
        _credentialListener = credentialListener;
        _attention = attention;
        _approvals = approvals;
        _cliFallback = cliFallback;
        _prompts = promptLibrary;
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

        // A brokered credential is being requested by the sandboxed agent, so this is the SessionAgent surface.
        // When that surface is gated (notifications/02 tier 2), route the share through the same durable
        // approval path as everything else in the inbox — the prompt becomes a durable request that survives the
        // user navigating away, rather than a live consent card that vanishes if unseen. Ungated (the default)
        // keeps the existing ask-once-per-repo live consent behaviour unchanged.
        if (_approvals is not null && _approvals.RequiresApproval(CredentialShareAction.Id, ApprovalSurface.SessionAgent))
        {
            var action = new CredentialShareAction(grant.SessionId, request.Host, request.Repo,
                () => session.RecordGitCredentialAsync(request.Host, request.Repo, allowed: true));
            return await _approvals.InvokeForDecisionAsync(action, ApprovalSurface.SessionAgent).ConfigureAwait(false);
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

    // Last-known auth status per adapter, populated lazily/in the background so ListAgents stays sync+fast.
    // Absence = not yet probed; a stored null = probed and the adapter has no reliable login signal (no badge).
    private readonly ConcurrentDictionary<string, ProviderAuthStatus?> _authStatus = new();
    private readonly ConcurrentDictionary<string, byte> _authProbing = new();

    public IReadOnlyList<AgentInfo> ListAgents()
    {
        // Kick off a one-time background probe per adapter so the badge fills in without a manual check.
        foreach (var adapter in _adapters.All)
        {
            if (!_authStatus.ContainsKey(adapter.Descriptor.Id))
            {
                _ = ProbeAuthInBackgroundAsync(adapter);
            }
        }

        return AgentSnapshot();
    }

    /// <summary>Builds the current agent list from cached auth status, without triggering probes (so it's
    /// safe to call from a broadcast).</summary>
    private IReadOnlyList<AgentInfo> AgentSnapshot() => _adapters.All.Select(BuildAgentInfo).ToArray();

    private AgentInfo BuildAgentInfo(IAgentAdapter a) => new(
        a.Descriptor.Id, a.Descriptor.DisplayName, a.Descriptor.Version,
        // When agents run in a sandbox, availability reflects the baked image (host PATH is
        // irrelevant — the agent runs in the VM, not on the host).
        Available: _images is not null
            ? (_projects is not null ? _images.ImageHasAgent(_projects.Default(), a.Descriptor.Id) : _images.ImageHasAgent(a.Descriptor.Id))
            : a.IsAvailable(),
        Auth: _authStatus.TryGetValue(a.Descriptor.Id, out var status) ? status : null);

    /// <summary>Probes one adapter's auth status once, caches it, and broadcasts an updated agent list when
    /// the adapter reports a status (so the picker gains its badge). Best-effort — failures cache null.</summary>
    private async Task ProbeAuthInBackgroundAsync(IAgentAdapter adapter)
    {
        var id = adapter.Descriptor.Id;
        if (!_authProbing.TryAdd(id, 0))
        {
            return; // a probe for this adapter is already in flight.
        }

        try
        {
            var status = await adapter.GetAuthStatusAsync().ConfigureAwait(false);
            _authStatus[id] = status;
            if (status is not null)
            {
                await _broadcaster.PublishAgentsChangedAsync(AgentSnapshot()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auth-status probe failed for {AdapterId}", id);
            _authStatus[id] = null;
        }
        finally
        {
            _authProbing.TryRemove(id, out _);
        }
    }

    /// <summary>Forces a fresh (cache-bypassing) auth-status check for one adapter, updates the cache, tells
    /// every client (so all pickers refresh), and returns the refreshed <see cref="AgentInfo"/>.</summary>
    public async Task<AgentInfo> CheckAuthStatusAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        var adapter = _adapters.Find(adapterId)
            ?? throw new InvalidOperationException($"Unknown agent adapter '{adapterId}'.");

        try
        {
            _authStatus[adapterId] = await adapter.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Forced auth-status check failed for {AdapterId}", adapterId);
            _authStatus[adapterId] = null;
        }

        await _broadcaster.PublishAgentsChangedAsync(AgentSnapshot()).ConfigureAwait(false);
        return BuildAgentInfo(adapter);
    }

    /// <summary>The models an adapter can be told to use: its live-probed list when it implements
    /// <see cref="IModelListingAdapter"/> and supports probing, else its static fallback. Empty for adapters
    /// with no model axis, and for unknown ids — so a client just shows no picker. A probe failure degrades to
    /// the static list rather than surfacing an error.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        if (_adapters.Find(adapterId) is not IModelListingAdapter lister)
        {
            return [];
        }

        try
        {
            return await ModelCatalog.ResolveAsync(lister, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Model listing failed for {AdapterId}; falling back to the static list", adapterId);
            return lister.StaticModels;
        }
    }

    /// <summary>Whether this host can isolate sessions in per-session sandbox VMs (a provider is configured).</summary>
    public bool SandboxAvailable => _sandboxes is not null;

    /// <summary>Whether the host refuses to run any session outside a sandbox (Agnes:Security:RequireSandbox).
    /// Surfaced to clients via <see cref="HostInfo.RequireSandbox"/> so the new-session UI can force the toggle
    /// on; still enforced host-side regardless of what any client sends.</summary>
    public bool SandboxRequired => _security.RequireSandbox;

    /// <summary>Whether the host forbids autonomous / skip-permissions sessions (Agnes:Security:RequirePermissionPrompts).
    /// Surfaced via <see cref="HostInfo.RequirePermissionPrompts"/> so the new-session UI can lock the permission
    /// toggle to attended; enforced host-side regardless.</summary>
    public bool PermissionPromptsRequired => _security.RequirePermissionPrompts;

    /// <summary>The recorded owner (principal id) and group (repo scope) of a session — for access decisions
    /// under session isolation. Both null for a session opened before ownership tracking, or with no resolvable
    /// identity / repo. Read from the in-memory catalogue (repopulated from the store on restart).</summary>
    public (string? Owner, string? Group) GetOwnership(string sessionId)
        => _catalog.TryGetValue(sessionId, out var r) ? (r.Owner, r.Group) : (null, null);

    /// <summary>
    /// Enforces the host's session-directory allowlist (Agnes:Security:AllowedSessionRoots): a no-op when no
    /// allowlist is configured, otherwise throws <see cref="SessionSecurityException"/> if the caller-supplied
    /// directory resolves outside every allowed root. Called at each open entry BEFORE any filesystem side
    /// effect (worktree creation, workspace copy) so a rejected request changes nothing on disk.
    /// </summary>
    private void EnforceDirectoryAllowed(string workingDirectory)
    {
        if (!SessionDirectoryPolicy.IsWithinAllowedRoots(workingDirectory, _security.AllowedSessionRoots))
        {
            _logger.LogWarning("Refused a session in '{Directory}': outside the configured allowed session roots.", workingDirectory);
            throw new SessionSecurityException(
                $"The working directory '{workingDirectory}' is not within any of this host's allowed session roots.");
        }
    }

    /// <summary>
    /// Enforces the host's autonomy guardrails for a session about to (re)launch (Agnes:Security). Refuses an
    /// autonomous (<c>--dangerously-skip-permissions</c>) session when the host requires per-tool permission
    /// prompts, or when it would run outside a sandbox and unsandboxed autonomy isn't explicitly allowed. A
    /// no-op for an attended session. <paramref name="willSandbox"/> is whether the session will actually run
    /// inside a sandbox.
    /// </summary>
    private void EnforceAutonomyPolicy(string sessionId, bool skipPermissions, bool willSandbox)
    {
        if (!skipPermissions)
        {
            return;
        }

        if (_security.RequirePermissionPrompts)
        {
            _logger.LogWarning("Refused autonomous session {SessionId}: this host requires per-tool permission prompts.", sessionId);
            throw new SessionSecurityException(
                "Refused an autonomous session: this host requires per-tool permission prompts, so skip-permissions mode is disabled.");
        }

        if (!willSandbox && !_security.AllowUnsandboxedSkipPermissions)
        {
            _logger.LogWarning("Refused unsandboxed autonomous session {SessionId}: skip-permissions is only allowed inside a sandbox.", sessionId);
            throw new SessionSecurityException(
                "Refused a skip-permissions (autonomous) session outside a sandbox: this host only allows autonomous mode inside a " +
                "sandbox (set Agnes:Security:AllowUnsandboxedSkipPermissions=true to override).");
        }
    }

    /// <summary>Which host-level plugin-point capabilities are actually populated right now (AC2/AC3 of
    /// .ideas/00-plugin-architecture.md) — queried live rather than cached, so it reflects the current
    /// registry state if plugins are ever installed/enabled/disabled without a restart.</summary>
    public IReadOnlyList<HostCapability> GetCapabilities() =>
    [
        new HostCapability(HostCapabilityIds.AgentAdapter, _adapters.All.Count > 0, FailClosed: true),
        new HostCapability(HostCapabilityIds.SandboxProvider, SandboxAvailable, FailClosed: false),
    ];

    public async Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null, string? owner = null, CancellationToken cancellationToken = default)
    {
        // Event spine: a plugin may redirect the adapter/working directory or veto the open.
        var open = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforeSessionOpenEvent(adapterId, workingDirectory), cancellationToken).ConfigureAwait(false);
        if (open.IsCanceled)
        {
            throw new InvalidOperationException(open.CancelReason is { Length: > 0 } r ? $"Opening a session was blocked: {r}" : "Opening a session was blocked by a plugin.");
        }

        adapterId = open.AdapterId;
        workingDirectory = open.WorkingDirectory;
        if (_adapters.Find(adapterId) is null)
        {
            throw new InvalidOperationException($"Unknown agent adapter '{adapterId}'.");
        }

        // Confine the session to an allowed root (if configured) before we create a worktree or launch anything.
        EnforceDirectoryAllowed(workingDirectory);

        var sessionId = Guid.NewGuid().ToString("n");
        var effectiveDirectory = workingDirectory;
        if (useWorktree)
        {
            var worktree = await _git.CreateWorktreeAsync(workingDirectory, sessionId, cancellationToken).ConfigureAwait(false);
            if (worktree is not null)
            {
                effectiveDirectory = worktree;
                State(sessionId).Worktree = (workingDirectory, worktree);
                _logger.LogInformation("Session {SessionId} isolated in worktree {Worktree}", sessionId, worktree);
            }
        }

        var info = await OpenSessionCoreAsync(
            sessionId, adapterId, effectiveDirectory, skipPermissions, mcpApproval, gitCredentialMode,
            useSandbox, modelId, existingSandbox: null, worktree: useWorktree, cancellationToken, owner: owner).ConfigureAwait(false);
        await _bus.DispatchAsync(new Agnes.Abstractions.Events.SessionOpenedEvent(info.SessionId, adapterId), cancellationToken).ConfigureAwait(false);
        return info;
    }

    // ---- Direct (external / "watch") sessions — .ideas/sessions/02-direct-vs-synced-sessions.md ----
    // Discovery + read-only watch of sessions a CLI created OUTSIDE Agnes (from the CLI's own on-disk logs).
    // Discovery unions across discovery-capable adapters; a watch tails the CLI's log into a fresh, read-only
    // Agnes session so all the normal snapshot/tail/multi-client machinery applies — without ever writing to
    // the underlying CLI.

    private const string ReadOnlyRejectionMessage =
        "This is a read-only view of a session running outside Agnes — watching only, so sending is disabled. " +
        "Adopt it to continue the conversation in Agnes.";

    /// <summary>
    /// Discovers sessions the installed CLIs created on their own (outside Agnes) for
    /// <paramref name="workspaceDirectory"/>, unioned across every adapter that implements
    /// <see cref="IExternalSessionSource"/>. Adapters without the capability contribute nothing (graceful), and
    /// a failing adapter is skipped rather than failing the whole query. Most-recent first.
    /// </summary>
    public async Task<IReadOnlyList<ExternalSessionInfo>> DiscoverExternalSessionsAsync(string workspaceDirectory, CancellationToken cancellationToken = default)
    {
        var discovered = new List<ExternalSessionInfo>();
        foreach (var adapter in _adapters.All)
        {
            if (adapter is not IExternalSessionSource source)
            {
                continue;
            }

            try
            {
                discovered.AddRange(await source.DiscoverAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "External-session discovery failed for adapter {AdapterId}", adapter.Descriptor.Id);
            }
        }

        return discovered.OrderByDescending(s => s.LastActivity).ToArray();
    }

    /// <summary>
    /// Opens a live, <b>read-only</b> Agnes session that watches the external CLI session
    /// <paramref name="externalId"/> (from <see cref="DiscoverExternalSessionsAsync"/>): the adapter tails the
    /// CLI's own log and its events are pumped into a new Agnes session log, so snapshot/tail/multi-client all
    /// work — but the session is flagged read-only, so prompts are rejected and the underlying CLI is never
    /// disturbed. The watch is live-only (not persisted for cross-restart resume): a Direct session is honestly
    /// unavailable once the host is offline.
    /// </summary>
    public async Task<SessionInfo> AttachExternalSessionAsync(string adapterId, string externalId, CancellationToken cancellationToken = default)
    {
        if (_adapters.Find(adapterId) is not IExternalSessionSource source)
        {
            throw new InvalidOperationException($"Agent '{adapterId}' can't watch externally-created sessions.");
        }

        var attachment = await source.AttachExternalSessionAsync(externalId, cancellationToken).ConfigureAwait(false);
        var sessionId = Guid.NewGuid().ToString("n");
        State(sessionId).ReadOnly = true;

        var session = TrackSession(sessionId, adapterId, attachment.WorkspaceDirectory, attachment.Session, wireLifecycle: false);
        _logger.LogInformation("Watching external session {ExternalId} on {AdapterId} as read-only session {SessionId}", externalId, adapterId, sessionId);

        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionInfo(sessionId, adapterId, attachment.WorkspaceDirectory, head,
            session.Modes, session.CurrentModeId, Sandbox: null, SkipPermissions: false, Project: null, ReadOnly: true);
    }

    /// <summary>
    /// SEAM (stretch, sessions/02): promote a read-only external watch into a fully-owned, <b>writable</b> Agnes
    /// session going forward — importing its transcript into a new event-sourced session and resuming the CLI's
    /// conversation under Agnes's control. The read-only import (discover + watch) is complete; the
    /// write-continuation is deliberately left unimplemented rather than faked, because it needs the adapter to
    /// resume a conversation from an <em>external</em> id, which no adapter yet exposes as a resume source (a
    /// CLI-log id isn't the same as the agent-reported session id Agnes resumes with today). Wiring this is the
    /// natural next step: import the watched session's events, then <c>StartSessionAsync</c> with a
    /// resume-from-external option once an adapter offers one.
    /// </summary>
    public Task<SessionInfo> AdoptExternalSessionAsync(string adapterId, string externalId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Folding an external session into a writable Agnes session isn't supported yet: watching it read-only " +
            "works now, but continuing it requires the adapter to resume the external conversation id, which isn't yet exposed.");

    /// <summary>
    /// The shared open path: resolve the project, ensure a checkout, provision (or adopt) a sandbox,
    /// launch the agent, and catalogue the session. <paramref name="existingSandbox"/> is a pre-made
    /// (e.g. CoW-cloned) sandbox to adopt instead of provisioning a fresh one; <paramref name="worktree"/>
    /// only flags the persisted record.
    /// </summary>
    private async Task<SessionInfo> OpenSessionCoreAsync(
        string sessionId, string adapterId, string effectiveDirectory,
        bool skipPermissions, string mcpApproval, string gitCredentialMode, bool useSandbox, string? modelId,
        ISandbox? existingSandbox, bool worktree, CancellationToken cancellationToken, string? resumeSessionId = null, string? owner = null)
    {
        var adapter = _adapters.Find(adapterId);
        if (adapter is null)
        {
            throw new InvalidOperationException($"Unknown agent adapter '{adapterId}'.");
        }

        // Host policy, checked here at the single shared open path so every entry — new, fork, cross-host
        // handoff — is covered, and before any project checkout / credential work happens. `willSandbox` is
        // whether this session will actually run inside a sandbox (an adopted / CoW-cloned VM counts).
        var willSandbox = existingSandbox is not null || (_sandboxes is not null && useSandbox);
        if (_security.RequireSandbox && !willSandbox)
        {
            var reason = _sandboxes is null
                ? "this host requires every session to run in a sandbox, but no sandbox provider is configured"
                : "this host requires every session to run in a sandbox";
            _logger.LogWarning("Refused an unsandboxed session {SessionId}: {Reason}.", sessionId, reason);
            throw new SessionSecurityException($"Refused to open an unsandboxed session: {reason}.");
        }

        EnforceAutonomyPolicy(sessionId, skipPermissions, willSandbox);

        // Resolve this session's project from the working directory's repo (auto-created + editable);
        // the sandbox / MCP / credential steps below use the project's own config.
        Projects.Project? project = null;
        string? group = null;
        if (_projects is not null)
        {
            var remote = await _git.GetRemoteUrlAsync(effectiveDirectory, cancellationToken).ConfigureAwait(false);
            var repoKey = GitRemote.TryParse(remote, out var remoteHost, out var remoteRepo) ? $"{remoteHost}/{remoteRepo}" : string.Empty;
            project = _projects.Resolve(repoKey);
            group = repoKey.Length == 0 ? null : repoKey; // the session's group id (repo scope) for group-based isolation.
            State(sessionId).Project = project;
            _logger.LogInformation("Session {SessionId} uses project '{Project}' ({Scope}).",
                sessionId, project.Name, repoKey.Length == 0 ? "default" : repoKey);
        }

        // Auto-checkout: if the project declares a repo and the working dir is empty, clone it (host-side,
        // scrubbed) before anything launches — so the agent starts on a ready checkout.
        await EnsureProjectCheckoutAsync(project, effectiveDirectory, sessionId, cancellationToken).ConfigureAwait(false);

        // Validate the MCP set up front: strict mode aborts here (naming the offending server) before any
        // sandbox/agent is provisioned; lenient mode emits a visible warning per skipped server.
        await ValidateMcpAsync(project, effectiveDirectory, sessionId, cancellationToken).ConfigureAwait(false);

        // Optionally provision a sandbox and run the agent inside it. Credentials and MCP config
        // (RunAt=Sandbox servers, plus RunAt=Host servers wired to the forward shim) are materialized
        // together in ONE bundle so the combined env write doesn't clobber the credential env file.
        ISandbox? sandbox = existingSandbox;
        string? mcpConfigPath = null;
        if (_sandboxes is not null && useSandbox)
        {
            if (sandbox is null)
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
            }

            _sandboxBySession[sessionId] = sandbox;

            // Record the sandbox so it stays visible/manageable (resume/delete) even after a restart.
            var now = DateTimeOffset.UtcNow;
            _sandboxRegistry?.Upsert(new SandboxRecord(
                sessionId, sandbox.Id, sandbox.Info.Provider, adapterId, effectiveDirectory,
                project?.Name, SandboxTitle(project, effectiveDirectory), "running", now, now,
                skipPermissions, mcpApproval, gitCredentialMode));

            // (Re-)stamp this session's own credentials + MCP + forward token into the sandbox. Critical
            // for a clone: it inherited the SOURCE session's tokens, which must be overwritten here.
            mcpConfigPath = await ProvisionSandboxContentsAsync(
                sandbox, sessionId, adapterId, effectiveDirectory, project, skipPermissions, mcpApproval, gitCredentialMode, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Session {SessionId} runs in sandbox {SandboxId}", sessionId, sandbox.Id);
        }
        else
        {
            mcpConfigPath = await MaterializeHostMcpAsync(adapterId, project, effectiveDirectory, cancellationToken).ConfigureAwait(false);
        }

        var agent = await adapter.StartSessionAsync(
            new AgentSessionOptions
            {
                WorkingDirectory = sandbox is null ? effectiveDirectory : "/work",
                Sandbox = sandbox,
                SkipPermissions = skipPermissions,
                McpConfigPath = mcpConfigPath,
                ModelId = modelId,
                // A native-fork handoff (connectivity/03) resumes the CLI's own conversation from the token
                // the source host exported; a plain open passes null and starts fresh.
                ResumeSessionId = resumeSessionId,
                // Prepend the library's enabled system-prompt additions; adapters whose CLI accepts a
                // system-prompt flag (e.g. Claude Code's --append-system-prompt) thread this through.
                SystemPrompt = _prompts?.AssembleSystemPromptAdditions(),
            },
            cancellationToken).ConfigureAwait(false);

        // Catalogue the session BEFORE tracking it: TrackSession starts the event pump, which fires
        // AgentSessionStarted (persisting the real agent session id) — that update must find the record.
        // Persist the initial (placeholder-id) record first so it can't overwrite the real id afterwards.
        var record = new SessionRecord(
            sessionId, adapterId, effectiveDirectory, agent.AgentSessionId,
            worktree, skipPermissions, sandbox is not null, DateTimeOffset.UtcNow, owner, group);
        _catalog[sessionId] = record;
        await _store.SaveSessionAsync(record, cancellationToken).ConfigureAwait(false);

        var session = TrackSession(sessionId, adapterId, effectiveDirectory, agent);
        _logger.LogInformation("Opened session {SessionId} on {AdapterId}", sessionId, adapterId);

        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionInfo(sessionId, adapterId, effectiveDirectory, head, agent.Modes, agent.CurrentModeId, MapSandbox(sandbox), skipPermissions, project?.Name);
    }

    /// <summary>Computes a fork plan for a live session: a proposed non-existing target folder (numeral-
    /// incremented sibling) and whether its sandbox can be copy-on-write cloned. Null if unknown.</summary>
    public ForkPlan? ProposeFork(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        var proposed = ForkNaming.Propose(session.WorkingDirectory);
        var canCopySandbox = _sandboxBySession.ContainsKey(sessionId) && _sandboxes is ISandboxCloner;
        return new ForkPlan(sessionId, session.WorkingDirectory, proposed, canCopySandbox);
    }

    /// <summary>
    /// Forks a session: copies its working folder to <paramref name="targetDirectory"/> (a faithful,
    /// independent snapshot including <c>.git</c> and untracked files) and opens a new session there,
    /// inheriting the source's agent and open-time options. When <paramref name="copySandbox"/> and the
    /// source is sandboxed on a cloner-capable provider, the VM is CoW-cloned and its work mount re-pointed
    /// at the copy; otherwise a fresh sandbox is provisioned (or the fork runs on the host if the source did).
    /// </summary>
    public async Task<SessionInfo> ForkSessionAsync(string sourceSessionId, string targetDirectory, bool copySandbox = true, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sourceSessionId, out var source))
        {
            throw new InvalidOperationException($"Unknown session '{sourceSessionId}'.");
        }

        var fork = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforeSessionForkEvent(sourceSessionId, targetDirectory), cancellationToken).ConfigureAwait(false);
        if (fork.IsCanceled)
        {
            throw new InvalidOperationException(fork.CancelReason is { Length: > 0 } r ? $"Forking was blocked: {r}" : "Forking was blocked by a plugin.");
        }

        targetDirectory = fork.TargetDirectory;
        // A fork writes a full copy of the workspace to an arbitrary destination — hold it to the allowlist too,
        // before any bytes are copied.
        EnforceDirectoryAllowed(targetDirectory);
        var adapterId = source.AdapterId;
        var sourceDir = source.WorkingDirectory;

        // A faithful copy of the working folder — uncommitted and untracked changes included.
        await DirectoryCopier.CopyAsync(sourceDir, targetDirectory, cancellationToken).ConfigureAwait(false);

        // Inherit the source's open-time options: skip-permissions from the session catalogue (always
        // present); MCP/credential modes from the sandbox registry (only meaningful when sandboxed).
        var skipPermissions = _catalog.TryGetValue(sourceSessionId, out var cat) && cat.SkipPermissions;
        var sandboxRecord = _sandboxRegistry?.Get(sourceSessionId);
        var mcpApproval = sandboxRecord?.McpApproval ?? "Ask";
        var gitCredentialMode = sandboxRecord?.GitCredentialMode ?? "Off";
        var sourceSandboxed = _sandboxBySession.TryGetValue(sourceSessionId, out var sourceSandbox);

        var sessionId = Guid.NewGuid().ToString("n");
        ISandbox? clonedSandbox = null;
        if (copySandbox && sourceSandboxed && sourceSandbox is not null && _sandboxes is ISandboxCloner cloner)
        {
            _logger.LogInformation("Forking session {Source} -> {Session} with a CoW sandbox clone", sourceSessionId, sessionId);
            clonedSandbox = await cloner.CloneAsync(
                sourceSandbox.Id, targetDirectory, new SandboxSpec { HostWorkingDirectory = targetDirectory }, cancellationToken).ConfigureAwait(false);
        }

        return await OpenSessionCoreAsync(
            sessionId, adapterId, targetDirectory, skipPermissions, mcpApproval, gitCredentialMode,
            useSandbox: sourceSandboxed, modelId: null, existingSandbox: clonedSandbox, worktree: false, cancellationToken,
            owner: cat?.Owner).ConfigureAwait(false); // a fork inherits the source session's owner.
    }

    /// <summary>Replay-fork: branch the conversation at a log point. Copies the workspace and inherits config
    /// exactly like <see cref="ForkSessionAsync"/>, then seeds the child agent with the parent's transcript
    /// up to <paramref name="atSequence"/> (invisibly — see <see cref="ForkedFromEvent"/>) and marks the
    /// child's log. Forking at a user message forks just before it and returns its text as an editable
    /// draft; forking at any other event forks after it with no draft.</summary>
    public async Task<Agnes.Protocol.ForkAtResult> ForkSessionAtAsync(string sourceSessionId, string targetDirectory, long atSequence, bool copySandbox = true, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(sourceSessionId, 0, cancellationToken).ConfigureAwait(false);
        var (seedEvents, draft) = SplitAtFork(snapshot.Events, atSequence);

        var childInfo = await ForkSessionAsync(sourceSessionId, targetDirectory, copySandbox, cancellationToken).ConfigureAwait(false);

        var marker = await _store.AppendAsync(childInfo.SessionId, new ForkedFromEvent(sourceSessionId, atSequence), cancellationToken).ConfigureAwait(false);
        await _broadcaster.PublishAsync(childInfo.SessionId, marker).ConfigureAwait(false);

        if (_sessions.TryGetValue(childInfo.SessionId, out var child) && BuildForkSeed(seedEvents) is { } seed)
        {
            child.SetPendingSeed([seed]);
        }

        return new Agnes.Protocol.ForkAtResult(childInfo, draft);
    }

    /// <summary>Which handoff support a given adapter offers: an adapter that implements
    /// <see cref="IHandoffCapableAdapter"/> reports its own <see cref="HandoffSupport"/>; every other adapter is
    /// <see cref="HandoffSupport.Unsupported"/> (never a silent failure — see <see cref="PrepareHandoffAsync"/>).</summary>
    public HandoffSupport HandoffSupportFor(string adapterId)
        => _adapters.Find(adapterId) is IHandoffCapableAdapter cap ? cap.Support : HandoffSupport.Unsupported;

    /// <summary>
    /// Source-host half of a cross-host handoff (connectivity/03): produces a portable <see cref="HandoffState"/>
    /// the target host feeds to <see cref="AcceptHandoffAsync"/>. A cross-host handoff is a fork whose child lives
    /// on another host — for <see cref="HandoffSupport.Replay"/> the seed is this session's own event log (reusing
    /// the same replay machinery as same-host forking); for <see cref="HandoffSupport.NativeFork"/> it's the CLI's
    /// authoritative resume token, exported via <see cref="IHandoffCapableAdapter.ExportHandoffStateAsync"/>.
    /// Refuses an <see cref="HandoffSupport.Unsupported"/> agent with a typed error (AC5).
    /// </summary>
    public async Task<HandoffState> PrepareHandoffAsync(string sourceSessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(sourceSessionId, 0, cancellationToken).ConfigureAwait(false);
        var adapterId = snapshot.Session.AdapterId;
        var support = HandoffSupportFor(adapterId);
        if (support == HandoffSupport.Unsupported)
        {
            throw new HandoffNotSupportedException(
                $"Agent '{adapterId}' does not support session handoff to another host.");
        }

        string? resumeToken = null;
        if (support == HandoffSupport.NativeFork
            && _adapters.Find(adapterId) is IHandoffCapableAdapter cap
            && _sessions.TryGetValue(sourceSessionId, out var live))
        {
            resumeToken = await cap.ExportHandoffStateAsync(live.Agent, cancellationToken).ConfigureAwait(false);
        }

        // Replay carries the transcript to reconstruct on the target; native-fork carries only the token.
        var seedEvents = support == HandoffSupport.Replay ? snapshot.Events : [];
        return new HandoffState(sourceSessionId, adapterId, support, snapshot.Session.WorkingDirectory, resumeToken, seedEvents);
    }

    /// <summary>
    /// Target-host half of a cross-host handoff (connectivity/03): opens a fresh session at
    /// <paramref name="targetWorkingDirectory"/> that continues the source's conversation. For
    /// <see cref="HandoffSupport.Replay"/> the source transcript is rendered into an invisible seed (the exact
    /// mechanism same-host forking uses — see <see cref="BuildForkSeed"/>/<see cref="HostSession.SetPendingSeed"/>)
    /// and a <see cref="ForkedFromEvent"/> marks the child's log; for <see cref="HandoffSupport.NativeFork"/> the
    /// session is resumed from the exported token. The workspace is transferred separately (see
    /// <see cref="Handoff.WorkspaceTransfer"/>), not here.
    /// </summary>
    public async Task<SessionInfo> AcceptHandoffAsync(
        HandoffState state, string targetWorkingDirectory, CancellationToken cancellationToken = default)
    {
        if (state.Mode == HandoffSupport.Unsupported)
        {
            throw new HandoffNotSupportedException(
                $"Handoff state for agent '{state.AdapterId}' is marked unsupported.");
        }

        if (_adapters.Find(state.AdapterId) is null)
        {
            throw new InvalidOperationException(
                $"This host has no adapter '{state.AdapterId}' to accept the handoff.");
        }

        // An accepted handoff opens a session at a directory chosen by the peer — confine it like any other.
        EnforceDirectoryAllowed(targetWorkingDirectory);

        var sessionId = Guid.NewGuid().ToString("n");
        var resumeSessionId = state.Mode == HandoffSupport.NativeFork ? state.ResumeToken : null;
        var info = await OpenSessionCoreAsync(
            sessionId, state.AdapterId, targetWorkingDirectory,
            skipPermissions: false, mcpApproval: "Ask", gitCredentialMode: "Off",
            useSandbox: false, modelId: null, existingSandbox: null, worktree: false,
            cancellationToken, resumeSessionId).ConfigureAwait(false);

        // Mark the child's log with its origin, then (for replay) seed the reconstructed transcript invisibly —
        // exactly as ForkSessionAtAsync does, so a client watching the new session sees the same history.
        var marker = await _store.AppendAsync(
            sessionId, new ForkedFromEvent(state.SourceSessionId, GetHead(state.SeedEvents)), cancellationToken).ConfigureAwait(false);
        await _broadcaster.PublishAsync(sessionId, marker).ConfigureAwait(false);

        if (state.Mode == HandoffSupport.Replay
            && _sessions.TryGetValue(sessionId, out var child)
            && BuildForkSeed(state.SeedEvents) is { } seed)
        {
            child.SetPendingSeed([seed]);
        }

        return info;
    }

    private static long GetHead(IReadOnlyList<SessionEvent> events)
        => events.Count == 0 ? 0 : events[^1].Sequence;

    // Fork "at a user message" forks BEFORE it (its text becomes an editable draft); forking at any other
    // event forks AFTER it (no draft).
    private static (IReadOnlyList<SessionEvent> Seed, string? Draft) SplitAtFork(IReadOnlyList<SessionEvent> events, long atSequence)
        => events.FirstOrDefault(e => e.Sequence == atSequence) is MessageChunkEvent { Role: MessageRole.User, Content: TextContent t }
            ? (events.Where(e => e.Sequence < atSequence).ToList(), t.Text)
            : (events.Where(e => e.Sequence <= atSequence).ToList(), null);

    // The parent transcript tail rendered as one text block the child agent reads as prior context.
    private static ContentBlock? BuildForkSeed(IReadOnlyList<SessionEvent> events)
    {
        const string header = "[Forked conversation — prior context follows]";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        foreach (var e in events)
        {
            switch (e)
            {
                case MessageChunkEvent { Content: TextContent t } m:
                    sb.Append(m.Role == MessageRole.User ? "User: " : "Assistant: ").AppendLine(t.Text);
                    break;
                case ToolCallEvent tc:
                    sb.Append("[tool: ").Append(tc.Title).AppendLine("]");
                    break;
            }
        }

        var text = sb.ToString().TrimEnd();
        return text.Length > header.Length ? new TextContent(text) : null;
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
    // one (so two projects can expose different servers), else the host's global registry (legacy). The
    // registry path is scope-filtered for the session's workspace and drops unresolvable enabled servers;
    // strict-mode failures are surfaced up front by ValidateMcpAsync, so this stays non-throwing.
    private IReadOnlyList<McpServerInfo> ApplicableMcp(Projects.Project? project, McpRunAt runAt, string? workspaceId)
    {
        IReadOnlyList<McpServerInfo> servers;
        if (project is not null)
        {
            var want = runAt == McpRunAt.Sandbox ? "sandbox" : "host";
            servers = project.McpServers.Where(s => s.Enabled && string.Equals(s.RunAt, want, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        else
        {
            servers = _mcp?.Resolve(runAt, workspaceId, strict: false).Servers ?? [];
        }

        // Operator allowlist (Agnes:Security:AllowedHostMcpServers): only named servers may run on the HOST
        // (outside the sandbox). This is the single seam feeding both the unsandboxed-direct and the sandboxed
        // host-forward paths, so a disallowed host server is never materialized, granted a forward token, or
        // spawned. Sandbox-run servers are unaffected. Drops are surfaced to the user by ValidateMcpAsync.
        if (runAt == McpRunAt.Host && _security.RestrictsHostMcpServers)
        {
            servers = servers.Where(s => _security.IsHostMcpServerAllowed(s.Name)).ToArray();
        }

        return servers;
    }

    /// <summary>
    /// Pre-flight the host registry's MCP resolution for a session about to start (the global-registry
    /// path only — a project's servers are already workspace-scoped by construction). In strict mode an
    /// unresolvable enabled server throws <see cref="McpResolutionException"/>, aborting the start with a
    /// clear cause; in lenient mode each skipped server is surfaced as a visible NoticeEvent so the user
    /// sees they're running with fewer tools than configured.
    /// </summary>
    private async Task ValidateMcpAsync(Projects.Project? project, string? workspaceId, string sessionId, CancellationToken cancellationToken)
    {
        // Operator host-MCP allowlist: name each host-run server we're dropping because it isn't allowlisted, so
        // the user sees they're running with fewer tools than configured. Covers both the project and global
        // server sets (ApplicableMcp already does the actual dropping for both spawn paths).
        if (_security.RestrictsHostMcpServers)
        {
            IEnumerable<McpServerInfo> configuredHost = project is not null
                ? project.McpServers.Where(s => s.Enabled && string.Equals(s.RunAt, "host", StringComparison.OrdinalIgnoreCase))
                : _mcp?.Resolve(McpRunAt.Host, workspaceId, strict: false).Servers ?? [];

            foreach (var dropped in configuredHost.Where(s => !_security.IsHostMcpServerAllowed(s.Name)))
            {
                await AppendNoticeAsync(sessionId,
                    $"MCP server '{dropped.Name}' wants to run on the host but isn't on this host's allowed-host-MCP list — skipping it.").ConfigureAwait(false);
            }
        }

        if (project is not null || _mcp is null)
        {
            return;
        }

        foreach (var runAt in new[] { McpRunAt.Host, McpRunAt.Sandbox })
        {
            var resolution = _mcp.Resolve(runAt, workspaceId, _mcpStrict); // strict: throws, naming the server
            foreach (var warning in resolution.Warnings)
            {
                await AppendNoticeAsync(sessionId, warning).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private string? AddSandboxMcp(string adapterId, ISandbox sandbox, string sessionId,
        bool skipPermissions, string mcpApproval, Projects.Project? project, string? workspaceId,
        Dictionary<string, string> env, List<SandboxCredentialFile> files)
    {
        if (McpTargetFor(adapterId) is not { } target)
        {
            return null;
        }

        var entries = new List<McpServerInfo>(ApplicableMcp(project, McpRunAt.Sandbox, workspaceId));

        // An autonomous session doesn't prompt per tool, so host servers are only forwarded to it
        // when the user has chosen to trust them (the "Ask vs Trust" preference). Attended sessions
        // always get forwarding — the agent's own permission protocol gates each tool call.
        var forwardAllowed = !skipPermissions || string.Equals(mcpApproval, "Trust", StringComparison.OrdinalIgnoreCase);
        var hostServers = _forward is not null && _forwardListener is not null && forwardAllowed
            ? ApplicableMcp(project, McpRunAt.Host, workspaceId)
            : [];
        if (hostServers.Count > 0)
        {
            var shimVmPath = $"{sandbox.HomeDirectory.TrimEnd('/')}/{McpForward.ShimHomeRelativePath}";
            files.Add(new SandboxCredentialFile(McpForward.ShimHomeRelativePath, McpForward.ShimScript));

            var token = _forward!.Register(hostServers, sessionId);
            State(sessionId).ForwardToken = token;
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
        State(sessionId).CredentialToken = token;

        env["AGNES_GIT_HOST"] = _credentialListener.AdvertiseHost;
        env["AGNES_GIT_PORT"] = _credentialListener.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["AGNES_GIT_TOKEN"] = token;

        files.Add(new SandboxCredentialFile(GitCredentialHelper.HelperHomeRelativePath, GitCredentialHelper.Script));
        files.Add(new SandboxCredentialFile(".gitconfig", GitCredentialHelper.GitConfig(sandbox.HomeDirectory, userName, userEmail)));
        _logger.LogInformation("Session {SessionId}: GitHub access on {Host} brokered ({Mode}, per-repo consent).", sessionId, host, mode);
    }

    /// <summary>Writes a host (non-sandbox) session's RunAt=Host MCP config to a temp file for the CLI flag.</summary>
    private async Task<string?> MaterializeHostMcpAsync(string adapterId, Projects.Project? project, string? workspaceId, CancellationToken cancellationToken)
    {
        if (McpTargetFor(adapterId) is not { UsesFlag: true })
        {
            return null; // only the config-flag (Claude) host path is wired; host-Codex/ACP deferred
        }

        var servers = ApplicableMcp(project, McpRunAt.Host, workspaceId);
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

            if (_adapters.Find(record.AdapterId) is null)
            {
                throw new InvalidOperationException($"Adapter '{record.AdapterId}' for session '{sessionId}' is no longer registered.");
            }

            // A sandboxed session needs its VM. We can relaunch + resume it when we still hold the live
            // handle (only the CLI died), OR the VM is recorded in the persisted registry (host restarted
            // — re-attach it by name below). Only give up when the VM truly can't be located.
            var canReattachSandbox = _sandboxBySession.ContainsKey(sessionId)
                || (_sandboxes is not null && _sandboxRegistry?.Get(sessionId) is not null);
            if (record.Sandboxed && !canReattachSandbox)
            {
                var notice = await _store.AppendAsync(sessionId, new NoticeEvent(
                    "This sandboxed session can't be resumed — its sandbox VM is no longer available. Fork it to continue in a new session.",
                    IsError: true)).ConfigureAwait(false);
                await _broadcaster.PublishAsync(sessionId, notice).ConfigureAwait(false);
                throw new InvalidOperationException("Sandbox VM not available for resume.");
            }

            // A cold-start re-attach (VM not currently held) can take a while — tell the client so the wait
            // isn't silent while the VM boots and the agent re-launches.
            if (record.Sandboxed && !_sandboxBySession.ContainsKey(sessionId))
            {
                var starting = await _store.AppendAsync(sessionId, new NoticeEvent(
                    "Resuming the sandbox and reconnecting the agent…")).ConfigureAwait(false);
                await _broadcaster.PublishAsync(sessionId, starting).ConfigureAwait(false);
            }

            _logger.LogInformation("Re-attaching agent for restored session {SessionId} (resume={Resume})", sessionId, record.AgentSessionId);
            var session = await RelaunchAgentAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// (Re)launches the agent for an already-catalogued session under the SAME session id, resuming the
    /// agent's own conversation (<c>--resume</c>) when its session id is known. Reuses a live sandbox
    /// handle (only the CLI died), else re-attaches the VM by name (cold-start), else runs on the host.
    /// Installs and returns the new <see cref="HostSession"/>. Callers hold <see cref="_attachGate"/>.
    /// </summary>
    private async Task<HostSession> RelaunchAgentAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetValue(sessionId, out var record))
        {
            throw new InvalidOperationException($"Unknown session '{sessionId}'.");
        }

        var adapter = _adapters.Find(record.AdapterId);
        if (adapter is null)
        {
            throw new InvalidOperationException($"Adapter '{record.AdapterId}' for session '{sessionId}' is no longer registered.");
        }

        var effectiveDirectory = record.WorkingDirectory;

        // Re-apply the host policy on resume too (the guardrails may have been turned on since this session was
        // first opened): a required-sandbox host refuses to relaunch a session that wasn't sandboxed, and the
        // directory allowlist still applies to the persisted working dir. Fork-into-a-sandbox is the escape hatch.
        var willSandbox = record.Sandboxed && _sandboxes is not null;
        if (_security.RequireSandbox && !willSandbox)
        {
            throw new SessionSecurityException(
                "This host now requires every session to run in a sandbox; this session predates that policy and " +
                "can't be resumed unsandboxed. Fork it to continue in a new, sandboxed session.");
        }

        EnforceDirectoryAllowed(effectiveDirectory);
        EnforceAutonomyPolicy(sessionId, record.SkipPermissions, willSandbox);

        var project = await ResolveProjectForAsync(sessionId, effectiveDirectory, cancellationToken).ConfigureAwait(false);

        // Re-validate MCP on restart too (config may have changed since first open): strict aborts, lenient warns.
        await ValidateMcpAsync(project, effectiveDirectory, sessionId, cancellationToken).ConfigureAwait(false);

        ISandbox? sandbox = null;
        string? mcpConfigPath;
        if (record.Sandboxed && _sandboxes is not null)
        {
            var sandboxRecord = _sandboxRegistry?.Get(sessionId);
            if (_sandboxBySession.TryGetValue(sessionId, out var existing))
            {
                sandbox = existing; // VM still up (the CLI died in place) — reuse it.
            }
            else if (sandboxRecord is not null)
            {
                sandbox = await _sandboxes.AttachAsync(
                    sandboxRecord.VmName, new SandboxSpec { HostWorkingDirectory = effectiveDirectory }, start: true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException($"No sandbox recorded for session '{sessionId}'.");
            }

            _sandboxBySession[sessionId] = sandbox;
            // Re-stamp credentials + MCP + forward token: the guest's /run is tmpfs (lost on a VM
            // cold-start) and re-provisioning is idempotent when the VM was still up.
            mcpConfigPath = await ProvisionSandboxContentsAsync(
                sandbox, sessionId, record.AdapterId, effectiveDirectory, project,
                record.SkipPermissions, sandboxRecord?.McpApproval ?? "Ask", sandboxRecord?.GitCredentialMode ?? "Off", cancellationToken).ConfigureAwait(false);
            _sandboxRegistry?.SetState(sessionId, "running", DateTimeOffset.UtcNow);
        }
        else
        {
            mcpConfigPath = await MaterializeHostMcpAsync(record.AdapterId, project, effectiveDirectory, cancellationToken).ConfigureAwait(false);
        }

        var agent = await adapter.StartSessionAsync(
            new AgentSessionOptions
            {
                WorkingDirectory = sandbox is null ? effectiveDirectory : "/work",
                Sandbox = sandbox,
                SkipPermissions = record.SkipPermissions,
                McpConfigPath = mcpConfigPath,
                // Only resume when the agent reported a real session id (a UUID); the pre-init placeholder
                // (a dash-less GUID) would make `--resume` fail, so start fresh in that case.
                ResumeSessionId = LooksResumable(record.AgentSessionId) ? record.AgentSessionId : null,
            },
            cancellationToken).ConfigureAwait(false);

        return TrackSession(sessionId, record.AdapterId, effectiveDirectory, agent);
    }

    /// <summary>Claude's real session id is a UUID (has dashes); our pre-init placeholder is a dash-less GUID.</summary>
    private static bool LooksResumable(string? agentSessionId)
        => !string.IsNullOrEmpty(agentSessionId) && agentSessionId.Contains('-', StringComparison.Ordinal);

    /// <summary>Builds a <see cref="HostSession"/>, wires crash-recovery, and tracks it as the live session.
    /// <paramref name="wireLifecycle"/> is false for a read-only Direct/watch session (sessions/02): its agent
    /// only tails an on-disk log, so crash-recovery, id-persistence, title-refresh and credential-recovery must
    /// NOT fire (they'd try to relaunch a real CLI). The event pump still appends + broadcasts the tailed
    /// events, so snapshot/tail/multi-client all work.</summary>
    private HostSession TrackSession(string sessionId, string adapterId, string workingDirectory, IAgentSession agent, bool wireLifecycle = true)
    {
        var session = new HostSession(
            sessionId, adapterId, workingDirectory, agent, _store, _broadcaster,
            _loggerFactory.CreateLogger<HostSession>(), _bus);
        if (wireLifecycle)
        {
            session.Faulted = () => _ = RecoverAgentAsync(sessionId);
            // Persist the agent's real session id the moment it reports one, so --resume works reliably.
            session.AgentSessionStarted = id => _ = UpdateAgentSessionIdAsync(sessionId, id);
            // After each turn, refresh the agent's auto-generated title (Claude's on-disk aiTitle).
            session.TurnCompleted = () => _ = MaybeUpdateTitleAsync(sessionId);
            // A credential fault the agent classifies as recoverable (e.g. a revoked/expired OAuth token
            // after the host rotated it): relaunch with freshly-materialized credentials so the new
            // process picks up a valid token.
            session.AgentError = message => _ = MaybeRecoverCredentialsAsync(sessionId, message);
        }

        // Re-apply the session's chosen send policy to the (possibly freshly rebuilt) live session.
        if (StateOrNull(sessionId) is { } state)
        {
            session.SendPolicy = state.SendPolicy;
        }

        _sessions[sessionId] = session;
        return session;
    }

    /// <summary>Auto-recovery: the agent's process died unexpectedly. Restart + resume it in place, unless it
    /// just did the same (a crash loop) — then pause and ask the user to restart manually.</summary>
    private Task RecoverAgentAsync(string sessionId) => RecoverAsync(
        sessionId,
        starting: "The agent stopped unexpectedly — restarting and resuming the conversation…",
        succeeded: "Agent restarted.",
        debounced: "The agent crashed again right after restarting. Automatic restart is paused — use “Restart agent” to try once more.");

    // An agent whose credentials went stale mid-session (e.g. a revoked/expired OAuth token) surfaces it as
    // an agent error. A sandboxed agent can't refresh in place (its token is baked into the launch env), so
    // relaunch it with freshly-materialized credentials — which pulls the current host token. The agent
    // classifies its own faults (IsRecoverableCredentialFault); the host doesn't pattern-match error text.
    private async Task MaybeRecoverCredentialsAsync(string sessionId, string message)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)
            || !_sandboxBySession.ContainsKey(sessionId)
            || _adapters.Find(session.AdapterId) is not { } adapter
            || !adapter.IsRecoverableCredentialFault(message))
        {
            return;
        }

        await RecoverAsync(
            sessionId,
            starting: "The agent's login token expired — refreshing credentials and reconnecting…",
            succeeded: "Reconnected with refreshed credentials — resend your message to continue.",
            debounced: "Still can’t authenticate after refreshing — the login on the host may have expired. Sign in again there, then use “Restart agent”.").ConfigureAwait(false);
    }

    // Shared recovery: tear down the current agent and relaunch + resume it (RelaunchAgentAsync
    // re-provisions credentials), with a debounce so a persistent failure doesn't thrash.
    private async Task RecoverAsync(string sessionId, string starting, string succeeded, string debounced)
    {
        await _attachGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.TryRemove(sessionId, out var stale))
            {
                await stale.DisposeAsync().ConfigureAwait(false); // kills the stale process tree
            }

            var now = DateTimeOffset.UtcNow;
            if (State(sessionId).LastRecoveryAt is { } last && now - last < RecoveryDebounce)
            {
                await AppendNoticeAsync(sessionId, debounced, isError: true).ConfigureAwait(false);
                return;
            }

            State(sessionId).LastRecoveryAt = now;
            await AppendNoticeAsync(sessionId, starting).ConfigureAwait(false);
            await RelaunchAgentAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            await AppendNoticeAsync(sessionId, succeeded).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-recovery failed for session {SessionId}", sessionId);
            await AppendNoticeAsync(sessionId, "Couldn't restart the agent automatically: " + ex.Message, isError: true).ConfigureAwait(false);
        }
        finally
        {
            _attachGate.Release();
        }
    }

    /// <summary>User-invoked restart: tears down the current agent (if any) and relaunches + resumes it,
    /// resetting the crash-loop guard. Used to recover after auto-restart gave up.</summary>
    public async Task RestartAgentAsync(string sessionId)
    {
        if (!await _bus.AllowsAsync(new Agnes.Abstractions.Events.BeforeAgentRestartEvent(sessionId)).ConfigureAwait(false))
        {
            return; // a plugin kept the current agent as-is
        }

        await _attachGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.TryRemove(sessionId, out var old))
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            State(sessionId).LastRecoveryAt = null;
            await AppendNoticeAsync(sessionId, "Restarting the agent…").ConfigureAwait(false);
            await RelaunchAgentAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            await AppendNoticeAsync(sessionId, "Agent restarted.").ConfigureAwait(false);
            await _bus.DispatchAsync(new Agnes.Abstractions.Events.AgentRestartedEvent(sessionId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual restart failed for session {SessionId}", sessionId);
            await AppendNoticeAsync(sessionId, "Couldn't restart the agent: " + ex.Message, isError: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _attachGate.Release();
        }
    }

    private async Task AppendNoticeAsync(string sessionId, string message, bool isError = false)
    {
        var stored = await _store.AppendAsync(sessionId, new NoticeEvent(message, isError)).ConfigureAwait(false);
        await _broadcaster.PublishAsync(sessionId, stored).ConfigureAwait(false);
    }

    // Native Claude's auto-generated title lives in its on-disk transcript (an "ai-title" line), not the
    // stream. After a turn we read it and, when it changes, emit a SessionTitleEvent so clients can name
    // the session. Claude-only for now (other agents keep the folder-derived name); best-effort — any
    // failure (no file yet, exec error) just leaves the title unchanged.
    private async Task MaybeUpdateTitleAsync(string sessionId)
    {
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var session)
                || session.AdapterId != "claude-code-native"
                || !LooksResumable(session.AgentSessionId)) // the transcript is named by the real session id
            {
                return;
            }

            string? title;
            if (_sandboxBySession.TryGetValue(sessionId, out var sandbox))
            {
                // Inside the sandbox the agent's cwd is /work; read just the ai-title lines over exec.
                var path = $"{sandbox.HomeDirectory.TrimEnd('/')}/{ClaudeTitle.TranscriptRelativePath("/work", session.AgentSessionId)}";
                var result = await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["sh", "-c", ClaudeTitle.TailTitleCommand(path)] }).ConfigureAwait(false);
                title = result.ExitCode == 0 ? ClaudeTitle.ParseLatestTitle(result.Stdout) : null;
            }
            else
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home))
                {
                    return;
                }

                var path = Path.Combine(home, ClaudeTitle.TranscriptRelativePath(session.WorkingDirectory, session.AgentSessionId));
                title = File.Exists(path) ? ClaudeTitle.ParseLatestTitle(await File.ReadAllTextAsync(path).ConfigureAwait(false)) : null;
            }

            if (string.IsNullOrWhiteSpace(title) || State(sessionId).Title == title)
            {
                return;
            }

            State(sessionId).Title = title;
            var stored = await _store.AppendAsync(sessionId, new SessionTitleEvent(title)).ConfigureAwait(false);
            await _broadcaster.PublishAsync(sessionId, stored).ConfigureAwait(false);
            _logger.LogInformation("Session {SessionId} titled '{Title}'", sessionId, title);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Title refresh failed for session {SessionId}", sessionId);
        }
    }

    // ---- sandbox lifecycle ----

    public async Task PauseSandboxAsync(string sessionId)
    {
        if (!await _bus.AllowsAsync(new Agnes.Abstractions.Events.BeforeSandboxPauseEvent(sessionId)).ConfigureAwait(false))
        {
            return; // a plugin kept the sandbox running
        }

        if (_sandboxBySession.TryGetValue(sessionId, out var sandbox) && sandbox is IPausableSandbox pausable)
        {
            await pausable.PauseAsync().ConfigureAwait(false);
        }
    }

    public async Task ResumeSandboxAsync(string sessionId)
    {
        if (!await _bus.AllowsAsync(new Agnes.Abstractions.Events.BeforeSandboxResumeEvent(sessionId)).ConfigureAwait(false))
        {
            return; // a plugin kept the sandbox paused
        }

        if (_sandboxBySession.TryGetValue(sessionId, out var sandbox) && sandbox is IPausableSandbox pausable)
        {
            await pausable.ResumeAsync().ConfigureAwait(false);
        }
    }

    public async Task DeleteSandboxAsync(string sessionId)
    {
        if (!await _bus.AllowsAsync(new Agnes.Abstractions.Events.BeforeSandboxDeleteEvent(sessionId)).ConfigureAwait(false))
        {
            return; // a plugin protected the sandbox from destruction
        }

        Forget(sessionId); // unregister brokered tokens + git consents, drop metadata.

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
        await _bus.DispatchAsync(new Agnes.Abstractions.Events.SandboxDeletedEvent(sessionId)).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit stop-on-close: end the running agent and shut the sandbox VM down (freeing CPU + RAM),
    /// but KEEP the VM and the session record so it can be resumed later. Non-destructive — only
    /// <see cref="DeleteSandboxAsync"/> actually destroys the sandbox.
    /// </summary>
    public async Task StopSessionAsync(string sessionId)
    {
        if (!await _bus.AllowsAsync(new Agnes.Abstractions.Events.BeforeSessionStopEvent(sessionId)).ConfigureAwait(false))
        {
            return; // a plugin kept the session running
        }

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

        await _bus.DispatchAsync(new Agnes.Abstractions.Events.SessionStoppedEvent(sessionId)).ConfigureAwait(false);
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

        var mcpConfigPath = AddSandboxMcp(adapterId, sandbox, sessionId, skipPermissions, mcpApproval, project, effectiveDirectory, env, files);
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
        if (!await _bus.AllowsAsync(new Agnes.Abstractions.Events.BeforeSessionResumeEvent(sessionId)).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Resuming session '{sessionId}' was blocked by a plugin.");
        }

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

        // Re-attach the VM (by name — works even after a host restart), re-provision, and relaunch the
        // agent under the same session id — resuming its conversation (--resume) when its id is known.
        await _attachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HostSession session;
        try
        {
            session = _sessions.TryGetValue(sessionId, out var live)
                ? live
                : await RelaunchAgentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _attachGate.Release();
        }

        _logger.LogInformation("Resumed session {SessionId}", sessionId);
        await _bus.DispatchAsync(new Agnes.Abstractions.Events.SessionResumedEvent(sessionId)).ConfigureAwait(false);
        var sandbox = _sandboxBySession.TryGetValue(sessionId, out var sb) ? sb : null;
        var head = await _store.GetHeadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var project = StateOrNull(sessionId)?.Project;
        return new SessionInfo(sessionId, record.AdapterId, "/work", head, session.Modes, session.CurrentModeId,
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
        State(sessionId).Project = project;
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
    {
        if (_sandboxBySession.TryGetValue(sessionId, out var sandbox))
        {
            return MapSandbox(sandbox);
        }

        // Dormant (restored-but-not-yet-resumed) sandboxed session: report it from the persisted registry
        // so the client still shows the sandbox chip (as stopped) before the VM is re-attached on prompt.
        return _sandboxRegistry?.Get(sessionId) is { } record
            ? new SandboxStatus(record.Provider, record.VmName, "Stopped")
            : null;
    }

    private static SandboxStatus? MapSandbox(ISandbox? sandbox)
        => sandbox is null ? null : new SandboxStatus(sandbox.Info.Provider, sandbox.Info.Id, sandbox.Info.State.ToString());

    public async Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
    {
        // A read-only Direct/watch session (sessions/02) must never send to the underlying CLI — reject the
        // prompt before it can reach the (tail-only) agent handle, surfacing why in the session log.
        if (IsReadOnly(sessionId))
        {
            await AppendNoticeAsync(sessionId, ReadOnlyRejectionMessage, isError: true).ConfigureAwait(false);
            return;
        }

        // The event spine: a plugin interceptor may rewrite the prompt or veto it before it's sent.
        var before = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforePromptEvent(sessionId, content)).ConfigureAwait(false);
        if (before.IsCanceled)
        {
            var message = before.CancelReason is { Length: > 0 } reason ? $"Prompt blocked: {reason}" : "Prompt was blocked by a plugin.";
            var notice = await _store.AppendAsync(sessionId, new NoticeEvent(message, IsError: true)).ConfigureAwait(false);
            await _broadcaster.PublishAsync(sessionId, notice).ConfigureAwait(false);
            return;
        }

        var session = await EnsureLiveAsync(sessionId).ConfigureAwait(false);
        content = before.Content;
        // The real agent session id is captured via HostSession.AgentSessionStarted (on the init line),
        // not polled here — polling raced the init line and persisted the placeholder id, which broke
        // --resume. Codex reports its id synchronously at open, so the catalogue is already correct there.
        await session.PromptAsync(content).ConfigureAwait(false);
    }

    // ---- CLI-fallback terminal (platform/03) ----
    // Both the in-session terminal and provider login funnel through OpenFallbackTerminalAsync below — the
    // single ICliFallback.OpenTerminalAsync spawn path, never a bespoke Process.Start (the reuse discipline
    // this feature exists to enforce). Output rides the session event stream as TerminalOutputEvents.

    /// <summary>Opens a CLI-fallback terminal in a session and returns its terminal id. A null command uses
    /// the session's default shell; a null working directory uses the session's own working directory. The
    /// terminal's output is appended to the session log as <see cref="TerminalOutputEvent"/>s (via
    /// <see cref="ITerminalOutputSource"/> when the handle streams it), replayed like any other event.</summary>
    public async Task<string> OpenTerminalAsync(string sessionId, string? command, IReadOnlyList<string>? arguments, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default)
    {
        var session = await EnsureLiveAsync(sessionId).ConfigureAwait(false);
        var fallback = session.CliFallback ?? _cliFallback
            ?? throw new InvalidOperationException("This host has no CLI-fallback terminal provider.");

        var options = new TerminalOptions
        {
            Command = string.IsNullOrWhiteSpace(command) ? DefaultShell() : command,
            Arguments = arguments ?? [],
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? session.WorkingDirectory : workingDirectory,
            Columns = columns,
            Rows = rows,
        };

        // Bind streamed output to THIS session's log — the "output rides the session event stream" contract.
        var handle = await OpenFallbackTerminalAsync(fallback, options, session.AppendTerminalOutputAsync, cancellationToken).ConfigureAwait(false);
        return handle.TerminalId;
    }

    /// <summary>Writes raw input bytes to an open fallback terminal (no-op if the id is unknown/closed).</summary>
    public Task WriteTerminalAsync(string sessionId, string terminalId, byte[] data)
        => _terminals.TryGetValue(terminalId, out var handle) ? handle.WriteAsync(data) : Task.CompletedTask;

    /// <summary>Resizes an open fallback terminal (no-op if the id is unknown/closed).</summary>
    public Task ResizeTerminalAsync(string sessionId, string terminalId, int columns, int rows)
        => _terminals.TryGetValue(terminalId, out var handle) ? handle.ResizeAsync(columns, rows) : Task.CompletedTask;

    /// <summary>
    /// Starts a provider CLI's interactive login through the SAME CLI-fallback terminal path as the in-session
    /// terminal (platform/03 reuse discipline) — never a bespoke <c>Process.Start</c>. The login terminal is
    /// surfaced as its own lightweight, client-visible session so the user can WATCH the CLI's login prompts and
    /// TYPE the responses many logins require: its output rides <see cref="TerminalOutputEvent"/>s through the
    /// normal snapshot/tail, and keystrokes/resizes route back to the login PTY via
    /// <see cref="WriteTerminalAsync"/>/<see cref="ResizeTerminalAsync"/>. The returned id is BOTH the session
    /// to subscribe to and the terminal to write to (they're one and the same), so the existing
    /// <c>TerminalPanelViewModel</c> binds to it unchanged. When the login CLI exits, the provider's login badge
    /// is refreshed for every client and the scratch session is torn down.
    /// </summary>
    public async Task<string> BeginProviderLoginAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        var adapter = _adapters.Find(adapterId)
            ?? throw new InvalidOperationException($"Unknown agent adapter '{adapterId}'.");
        if (adapter.GetInteractiveLoginCommand() is not { } login)
        {
            throw new InvalidOperationException($"Agent '{adapterId}' has no interactive login command.");
        }

        var fallback = _cliFallback
            ?? throw new InvalidOperationException("This host has no CLI-fallback terminal provider for provider login.");

        var options = new TerminalOptions
        {
            Command = login.Command,
            Arguments = login.Arguments,
            WorkingDirectory = Path.GetTempPath(),
        };

        // Bind the login PTY's output to a fresh scratch session, keyed by the terminal id itself — so the one
        // returned string is both the session id a client subscribes to and the terminal id it writes back to.
        // The session is created right after the handle so its id is known; the sink reads it at emit-time (a
        // real login CLI produces nothing before then). Output flows through HostSession.AppendTerminalOutputAsync
        // — the exact same TerminalOutputEvent path as the in-session terminal, never a parallel channel.
        HostSession? loginSession = null;
        var handle = await OpenFallbackTerminalAsync(
            fallback, options,
            onOutput: (terminalId, data) => loginSession?.AppendTerminalOutputAsync(terminalId, data) ?? Task.CompletedTask,
            cancellationToken).ConfigureAwait(false);

        // The scratch session has no real agent — a no-op IAgentSession that emits nothing — and no lifecycle
        // wiring (no crash-recovery/resume/title): it exists purely to make the login terminal a client-visible,
        // interactive session. Its adapter id is the provider being signed into (so the snapshot names it).
        loginSession = TrackSession(handle.TerminalId, adapterId, options.WorkingDirectory,
            new LoginTerminalSession(handle.TerminalId), wireLifecycle: false);

        // When the login CLI exits, refresh the provider's login badge everywhere and tear the scratch session
        // down. A handle that can't observe its process exit simply leaves the session open until host shutdown.
        if (handle is ITerminalExitSource exit)
        {
            exit.Exited += () => _ = OnLoginTerminalExitedAsync(adapterId, handle.TerminalId);
        }

        return handle.TerminalId;
    }

    // The login CLI exited: forget the scratch session + its PTY handle, then force a fresh auth-status check so
    // every client's login badge reflects the new state. Best-effort — teardown/probe failures are swallowed.
    private async Task OnLoginTerminalExitedAsync(string adapterId, string loginTerminalId)
    {
        if (_sessions.TryRemove(loginTerminalId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (_terminals.TryRemove(loginTerminalId, out var handle))
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await CheckAuthStatusAsync(adapterId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Refreshing auth status after provider login for {AdapterId} failed", adapterId);
        }
    }

    // The single CLI-fallback spawn path. Opens the PTY, tracks the handle for later write/resize, and — when
    // the handle streams output and a sink was given — forwards each chunk to it (the session log).
    private async Task<ITerminalHandle> OpenFallbackTerminalAsync(ICliFallback fallback, TerminalOptions options, Func<string, string, Task>? onOutput, CancellationToken cancellationToken)
    {
        var handle = await fallback.OpenTerminalAsync(options, cancellationToken).ConfigureAwait(false);
        _terminals[handle.TerminalId] = handle;
        if (onOutput is not null && handle is ITerminalOutputSource source)
        {
            source.OutputReceived += (terminalId, data) => _ = onOutput(terminalId, data);
        }

        return handle;
    }

    // The host's default interactive shell for a bare "open a terminal" request.
    private static string DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return "powershell.exe";
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(shell) ? "/bin/bash" : shell;
    }

    // ---- pending queue & send policy (sessions/03) ----
    // One ordered queue per SESSION (not per client), owned by the live HostSession; queue mutations ride
    // the event log as PendingQueueEvent snapshots, so multi-client sync is automatic.

    /// <summary>Sets the session's send policy (what a send does while a turn is active). Stored durably so
    /// it survives an agent relaunch, and applied to the live session immediately.</summary>
    public async Task SetSendPolicyAsync(string sessionId, SendPolicy policy)
    {
        State(sessionId).SendPolicy = policy;
        if (_sessions.TryGetValue(sessionId, out var live))
        {
            live.SendPolicy = policy;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Submits a message under the session's send policy: queued, sent now, or interrupt-and-send.</summary>
    public async Task EnqueuePendingMessageAsync(string sessionId, IReadOnlyList<ContentBlock> content)
    {
        if (IsReadOnly(sessionId))
        {
            await AppendNoticeAsync(sessionId, ReadOnlyRejectionMessage, isError: true).ConfigureAwait(false);
            return;
        }

        await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).SubmitAsync(content).ConfigureAwait(false);
    }

    /// <summary>Moves a queued message to a new position in the session's pending queue.</summary>
    public async Task ReorderPendingMessageAsync(string sessionId, string messageId, int newIndex)
        => await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).ReorderPendingAsync(messageId, newIndex).ConfigureAwait(false);

    /// <summary>Interrupts the current turn and sends the named queued message ahead of the rest.</summary>
    public async Task SendPendingNowAsync(string sessionId, string messageId)
        => await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).SendPendingNowAsync(messageId).ConfigureAwait(false);

    /// <summary>Removes a queued message from the session's pending queue.</summary>
    public async Task RemovePendingMessageAsync(string sessionId, string messageId)
        => await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).RemovePendingAsync(messageId).ConfigureAwait(false);

    // Persist the agent's real session id once it reports one (a native CLI's init line) so the agent can
    // be resumed (--resume) after a crash or restart. Only overwrites a different, non-empty id.
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

    /// <summary>
    /// Materializes a client-uploaded attachment to a gitignored dir under the session's workspace and
    /// returns the workspace-relative path the agent should reference (never inline binary). Writes to the
    /// host-side working directory, which is bind-mounted to /work inside a sandbox, so one path serves both
    /// local and sandboxed sessions. The filename is stripped to its leaf and resolved through
    /// <see cref="Files.WorkspacePaths"/>, so nothing can be written outside the workspace.
    /// </summary>
    public async Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data, AttachmentConflict conflict = AttachmentConflict.KeepBoth)
    {
        var hostDir = WorkingDirectoryOf(sessionId);
        var attachDir = Files.WorkspacePaths.ResolveWithin(hostDir, Path.Combine(".agnes", "attachments"))
            ?? throw new InvalidOperationException("Could not resolve the attachments directory within the workspace.");
        Directory.CreateDirectory(attachDir);

        var leaf = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            leaf = "attachment";
        }

        var target = ResolveAttachmentTarget(attachDir, leaf, conflict);
        if (target is not null)
        {
            await File.WriteAllBytesAsync(target, data).ConfigureAwait(false);
        }

        // Workspace-relative, POSIX-separated (the agent sees it at /work/... inside the sandbox).
        var finalPath = target ?? Path.Combine(attachDir, leaf);
        return Path.GetRelativePath(hostDir, finalPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    // Applies the conflict policy, returning the path to write to — or null when the policy is Skip and the
    // file already exists (keep the original, write nothing).
    private static string? ResolveAttachmentTarget(string dir, string leaf, AttachmentConflict conflict)
    {
        var direct = Path.Combine(dir, leaf);
        if (!File.Exists(direct))
        {
            return direct;
        }

        return conflict switch
        {
            AttachmentConflict.Replace => direct,
            AttachmentConflict.Skip => null,
            _ => UniqueName(dir, leaf), // KeepBoth
        };
    }

    private static string UniqueName(string dir, string leaf)
    {
        var stem = Path.GetFileNameWithoutExtension(leaf);
        var ext = Path.GetExtension(leaf);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    // ---- file browser (see .ideas/git-and-files/03-attachments-and-file-browser.md) ----
    // Structured file ops over the session's working directory. Each delegates to Files.WorkspaceBrowser,
    // which routes every client-supplied relative path through the shared Files.WorkspacePaths guard, so a
    // `..`-escaping path is rejected before any disk access — the same guard UploadAttachmentAsync uses.

    /// <summary>Lists a directory in the session's workspace (empty path = the root), directories first.</summary>
    public Task<IReadOnlyList<Agnes.Protocol.FileEntry>> ListDirectoryAsync(string sessionId, string relativePath)
        => Task.FromResult(Files.WorkspaceBrowser.List(WorkingDirectoryOf(sessionId), relativePath));

    /// <summary>Reads a file in the session's workspace for preview (text, or bytes + mime for an image).</summary>
    public Task<Agnes.Protocol.FileContent> ReadFileAsync(string sessionId, string relativePath)
        => Task.FromResult(Files.WorkspaceBrowser.Read(WorkingDirectoryOf(sessionId), relativePath));

    /// <summary>Writes UTF-8 text to a file in the session's workspace (quick edit without an agent turn).</summary>
    public Task WriteFileAsync(string sessionId, string relativePath, string content)
    {
        Files.WorkspaceBrowser.Write(WorkingDirectoryOf(sessionId), relativePath, content);
        return Task.CompletedTask;
    }

    /// <summary>Creates a directory (and any missing parents) in the session's workspace.</summary>
    public Task CreateDirectoryAsync(string sessionId, string relativePath)
    {
        Files.WorkspaceBrowser.CreateDirectory(WorkingDirectoryOf(sessionId), relativePath);
        return Task.CompletedTask;
    }

    /// <summary>Renames/moves a file or directory within the session's workspace.</summary>
    public Task RenameEntryAsync(string sessionId, string fromRelativePath, string toRelativePath)
    {
        Files.WorkspaceBrowser.Rename(WorkingDirectoryOf(sessionId), fromRelativePath, toRelativePath);
        return Task.CompletedTask;
    }

    /// <summary>Deletes a file or directory (recursively) from the session's workspace.</summary>
    public Task DeleteEntryAsync(string sessionId, string relativePath)
    {
        Files.WorkspaceBrowser.Delete(WorkingDirectoryOf(sessionId), relativePath);
        return Task.CompletedTask;
    }

    /// <summary>Reads a workspace file's raw bytes for download.</summary>
    public Task<byte[]> DownloadFileAsync(string sessionId, string relativePath)
        => Task.FromResult(Files.WorkspaceBrowser.Download(WorkingDirectoryOf(sessionId), relativePath));

    /// <summary>The session's current read state (highest-viewed sequence + sticky-unread flag).</summary>
    public (long ReadCursor, bool StickyUnread) GetReadState(string sessionId)
        => StateOrNull(sessionId) is { } s ? (s.ReadCursor, s.StickyUnread) : (0, false);

    /// <summary>Advances the read cursor to <paramref name="sequence"/> (clearing any sticky-unread) and
    /// syncs it to the session's subscribed clients.</summary>
    public async Task MarkReadAsync(string sessionId, long sequence)
    {
        var s = State(sessionId);
        s.ReadCursor = Math.Max(s.ReadCursor, sequence);
        s.StickyUnread = false;
        await _broadcaster.PublishReadStateAsync(sessionId, s.ReadCursor, s.StickyUnread).ConfigureAwait(false);
    }

    /// <summary>Marks the session unread (sticky — stays unread while open until the next mark-read).</summary>
    public async Task MarkUnreadAsync(string sessionId)
    {
        var s = State(sessionId);
        s.StickyUnread = true;
        await _broadcaster.PublishReadStateAsync(sessionId, s.ReadCursor, s.StickyUnread).ConfigureAwait(false);
    }

    public async Task CancelAsync(string sessionId)
    {
        if (!await _bus.AllowsAsync(new Agnes.Abstractions.Events.BeforeSessionCancelEvent(sessionId)).ConfigureAwait(false))
        {
            return; // a plugin kept the turn running
        }

        await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).CancelAsync().ConfigureAwait(false);
    }

    public async Task SetModeAsync(string sessionId, string modeId)
    {
        var before = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforeModeChangeEvent(sessionId, modeId)).ConfigureAwait(false);
        if (before.IsCanceled)
        {
            return; // a plugin blocked the mode change
        }

        await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).SetModeAsync(before.ModeId).ConfigureAwait(false);
    }

    public Task<Agnes.Protocol.GitStatus> GetGitStatusAsync(string sessionId)
        => _git.GetStatusAsync(WorkingDirectoryOf(sessionId));

    /// <summary>Commits from a client (a human clicking "commit") — the default, ungated surface. Kept as the
    /// original signature so every existing caller is unchanged.</summary>
    public Task<Agnes.Protocol.GitCommitResult> GitCommitAsync(string sessionId, string message)
        => GitCommitAsync(sessionId, message, ApprovalSurface.Client);

    /// <summary>
    /// Commits, but expressed as an approval-gated action (notifications/02 tier 2) so the same operation can be
    /// gated per <paramref name="surface"/>. When the gate requires approval for this surface, the commit does
    /// NOT happen: a durable <see cref="ApprovalRequest"/> is created and surfaced in the inbox, and the result
    /// reports that approval is pending. When the surface is ungated (the default for everything unless a gate
    /// is configured) it commits immediately, exactly as before.
    /// </summary>
    public async Task<Agnes.Protocol.GitCommitResult> GitCommitAsync(string sessionId, string message, ApprovalSurface surface)
    {
        if (_approvals is not null && _approvals.RequiresApproval(GitCommitAction.Id, surface))
        {
            var action = new GitCommitAction(sessionId, message, CommitInternalAsync);
            var request = await _approvals.InvokeAsync(action, surface).ConfigureAwait(false);
            return new Agnes.Protocol.GitCommitResult(false,
                request is null
                    ? "Commit was blocked."
                    : $"Commit requires approval — waiting on request {request.Id}.");
        }

        return await CommitInternalAsync(sessionId, message, CancellationToken.None).ConfigureAwait(false);
    }

    // The actual commit path (bus veto → git commit → committed fact). Shared by an immediate commit and by a
    // gated commit that a human later approves, so both run byte-for-byte the same code.
    private async Task<Agnes.Protocol.GitCommitResult> CommitInternalAsync(string sessionId, string message, CancellationToken cancellationToken)
    {
        var before = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforeGitCommitEvent(sessionId, message)).ConfigureAwait(false);
        if (before.IsCanceled)
        {
            return new Agnes.Protocol.GitCommitResult(false, before.CancelReason is { Length: > 0 } r ? $"Commit blocked: {r}" : "Commit was blocked by a plugin.");
        }

        var result = await _git.CommitAsync(WorkingDirectoryOf(sessionId), before.Message, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            await _bus.DispatchAsync(new Agnes.Abstractions.Events.GitCommittedEvent(sessionId, before.Message)).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Resolves an approval-gated action from the inbox (notifications/02 tier 2): approve runs the
    /// parked action, reject turns it down. A no-op when approval gating isn't wired.</summary>
    public async Task ResolveGatedApprovalAsync(string requestId, bool approve, CancellationToken cancellationToken = default)
    {
        if (_approvals is null)
        {
            return;
        }

        if (approve)
        {
            await _approvals.ApproveAsync(requestId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _approvals.Reject(requestId);
        }
    }

    /// <summary>
    /// The changed-file set for a session, scoped per <paramref name="scope"/>. <see
    /// cref="ChangedFileScope.WholeRepo"/> is the git working-tree status; the narrower scopes are answered from
    /// the event-sourced log (the files the agent's tool calls touched this turn / this session), so a scope
    /// tighter than the whole repository is a query over history rather than a new tracking subsystem. Paths are
    /// normalized relative to the working directory (POSIX-separated) so every scope's set is comparable.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string sessionId, ChangedFileScope scope, CancellationToken cancellationToken = default)
    {
        var workingDirectory = WorkingDirectoryOf(sessionId);
        if (scope == ChangedFileScope.WholeRepo)
        {
            var status = await _git.GetStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            return status.Changes.Select(change => change.Path).Distinct(StringComparer.Ordinal).ToArray();
        }

        var events = await _store.ReadSinceAsync(sessionId, 0, cancellationToken).ConfigureAwait(false);
        return scope == ChangedFileScope.ThisTurn
            ? ChangedFileScoping.ThisTurn(events, workingDirectory)
            : ChangedFileScoping.ThisSession(events, workingDirectory);
    }

    /// <summary>
    /// Generates a suggested commit message by summarizing the session's <em>staged</em> diff through the shared
    /// one-shot-agent primitive (a bounded, non-interactive run over the diff, torn down afterward). Returns a
    /// suggestion with <see cref="Agnes.Protocol.CommitMessageSuggestion.HasSuggestion"/> false when nothing is
    /// staged (or no agent is available). This only <em>suggests</em> — the user still edits/confirms and commits.
    /// </summary>
    public async Task<Agnes.Protocol.CommitMessageSuggestion> GenerateCommitMessageAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var workingDirectory = WorkingDirectoryOf(sessionId);
        var diff = await _git.GetStagedDiffAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(diff))
        {
            return new Agnes.Protocol.CommitMessageSuggestion(false, string.Empty);
        }

        if (AdapterFor(sessionId) is not { } adapter)
        {
            return new Agnes.Protocol.CommitMessageSuggestion(false, string.Empty);
        }

        var prompt = new ContentBlock[] { new TextContent(CommitMessagePrompt(diff)) };
        var result = await _oneShot.RunAsync(adapter, workingDirectory, prompt, cancellationToken).ConfigureAwait(false);
        var message = result.Text.Trim();
        return message.Length > 0
            ? new Agnes.Protocol.CommitMessageSuggestion(true, message)
            : new Agnes.Protocol.CommitMessageSuggestion(false, string.Empty);
    }

    // Resolves the agent adapter backing a session (from the live handle or the catalogue), or null if unknown.
    private IAgentAdapter? AdapterFor(string sessionId)
    {
        var adapterId = _sessions.TryGetValue(sessionId, out var live) ? live.AdapterId
            : _catalog.TryGetValue(sessionId, out var record) ? record.AdapterId
            : null;
        return adapterId is null ? null : _adapters.Find(adapterId);
    }

    // A bounded prompt asking the agent to summarize a staged diff as a Conventional Commit message. The diff is
    // capped so a huge staging area can't blow the one-shot turn's context budget.
    private static string CommitMessagePrompt(string stagedDiff)
    {
        const int maxDiffChars = 12000;
        var diff = stagedDiff.Length > maxDiffChars
            ? string.Concat(stagedDiff.AsSpan(0, maxDiffChars), "\n… (diff truncated)")
            : stagedDiff;
        return
            "Write a git commit message for the following staged changes. Use the Conventional Commits format: " +
            "a concise `type(scope): summary` subject line under about 72 characters, then an optional blank line " +
            "and a short body explaining the why. Reply with ONLY the commit message text — no code fences, no " +
            "preamble, no explanation.\n\n```diff\n" + diff + "\n```";
    }

    /// <summary>Stashes the session working tree's uncommitted changes; null when there's nothing to stash.</summary>
    public Task<Agnes.Protocol.GitStashInfo?> GitStashAsync(string sessionId)
        => _git.StashAsync(WorkingDirectoryOf(sessionId));

    /// <summary>Restores a previously created stash (by its sha) in the session working directory.</summary>
    public Task<Agnes.Protocol.GitOperationResult> GitPopStashAsync(string sessionId, string stashId)
        => _git.PopStashAsync(WorkingDirectoryOf(sessionId), stashId);

    /// <summary>Switches the session working directory to another branch, optionally carrying a stash across.</summary>
    public Task<Agnes.Protocol.GitSwitchResult> GitSwitchBranchAsync(string sessionId, string branch, bool carryStash)
        => _git.SwitchBranchAsync(WorkingDirectoryOf(sessionId), branch, carryStash);

    /// <summary>Fast-forward-only pull; a diverged remote is refused with a typed error (never merged/rebased).</summary>
    public Task<Agnes.Protocol.GitPullResult> GitPullAsync(string sessionId)
        => _git.FastForwardPullAsync(WorkingDirectoryOf(sessionId));

    /// <summary>Pushes the session's current branch, publishing it upstream when requested.</summary>
    public Task<Agnes.Protocol.GitOperationResult> GitPushAsync(string sessionId, bool publishBranch)
        => _git.PushAsync(WorkingDirectoryOf(sessionId), publishBranch);

    /// <summary>Open pull requests on the forge that owns the session's <c>origin</c> remote; empty if the
    /// remote isn't recognized by any registered forge provider.</summary>
    public async Task<IReadOnlyList<Agnes.Abstractions.PullRequestInfo>> ListPullRequestsAsync(string sessionId)
    {
        var (provider, remote) = await ResolveForgeAsync(sessionId).ConfigureAwait(false);
        return provider is null || remote is null ? [] : await provider.ListOpenPullRequestsAsync(remote).ConfigureAwait(false);
    }

    /// <summary>Fetches and checks out a PR into the session's working directory, via its forge provider.</summary>
    public async Task<Agnes.Protocol.GitOperationResult> CheckoutPullRequestAsync(string sessionId, string pullRequestId)
    {
        var (provider, remote) = await ResolveForgeAsync(sessionId).ConfigureAwait(false);
        if (provider is null || remote is null)
        {
            return new Agnes.Protocol.GitOperationResult(false, "No forge provider matches this session's git remote.");
        }

        return await provider.CheckoutPullRequestAsync(WorkingDirectoryOf(sessionId), pullRequestId).ConfigureAwait(false);
    }

    /// <summary>Resolves the session's <c>origin</c> remote and the forge provider that owns its host.</summary>
    private async Task<(IGitHostProvider? Provider, string? Remote)> ResolveForgeAsync(string sessionId)
    {
        var remote = await _git.GetRemoteUrlAsync(WorkingDirectoryOf(sessionId)).ConfigureAwait(false);
        if (remote is null || !GitRemote.TryParse(remote, out var host, out _))
        {
            return (null, null);
        }

        return (_gitHosts.FirstOrDefault(p => p.Matches(host)), remote);
    }

    public async Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
    {
        var before = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforePermissionResponseEvent(sessionId, requestId, optionId)).ConfigureAwait(false);
        if (before.IsCanceled)
        {
            return; // a plugin blocked the response (the request stays pending)
        }

        await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).RespondToPermissionAsync(requestId, before.OptionId).ConfigureAwait(false);
    }

    public async Task AnswerQuestionAsync(string sessionId, string requestId, IReadOnlyList<Agnes.Protocol.QuestionAnswerDto> answers)
    {
        var mapped = (IReadOnlyList<Agnes.Abstractions.QuestionAnswer>)answers
            .Select(a => new Agnes.Abstractions.QuestionAnswer(a.QuestionId, a.SelectedLabels, a.Notes))
            .ToList();

        var before = await _bus.DispatchAsync(new Agnes.Abstractions.Events.BeforeQuestionAnswerEvent(sessionId, requestId, mapped)).ConfigureAwait(false);
        if (before.IsCanceled)
        {
            return; // a plugin blocked the answer
        }

        await (await EnsureLiveAsync(sessionId).ConfigureAwait(false)).AnswerQuestionAsync(requestId, before.Answers).ConfigureAwait(false);
    }

    /// <summary>Whether this host knows the session at all — live or dormant (restored-but-not-yet-prompted).
    /// Used by the push interactive-action guard to reject an action naming an unrecognized session before it
    /// could ever reach the live agent.</summary>
    public bool KnowsSession(string sessionId)
        => _sessions.ContainsKey(sessionId) || _catalog.ContainsKey(sessionId);

    /// <summary>Whether the session is currently live (loaded with a running agent handle), as opposed to
    /// dormant/catalogued-only. This is the "active session" the sharing layer requires before permission-approval
    /// rights can be granted to a collaborator — a dormant session has no tool prompts to answer.</summary>
    public bool IsSessionLive(string sessionId) => _sessions.ContainsKey(sessionId);

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
            live?.Modes, live?.CurrentModeId, GetSandboxStatus(sessionId), skipPermissions, Project: null, ReadOnly: IsReadOnly(sessionId));
        return new SessionSnapshot(info, events, head);
    }

    /// <summary>
    /// A lightweight listing of every known session (live or dormant-but-catalogued): its adapter, current
    /// auto-title, coarse status (<c>working</c> while a turn is running, <c>idle</c> when live-but-quiet,
    /// <c>dormant</c> when catalogued but not currently loaded), head sequence and current mode. Used by the
    /// MCP server's <c>list_sessions</c>/<c>get_session_status</c> tools; a pure aggregation over existing
    /// state that creates nothing.
    /// </summary>
    public async Task<IReadOnlyList<SessionListEntry>> ListSessionSummariesAsync(CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<string>(_catalog.Keys, StringComparer.Ordinal);
        foreach (var liveId in _sessions.Keys)
        {
            ids.Add(liveId);
        }

        var result = new List<SessionListEntry>(ids.Count);
        foreach (var id in ids)
        {
            var live = _sessions.TryGetValue(id, out var s) ? s : null;
            var adapterId = live?.AdapterId
                ?? (_catalog.TryGetValue(id, out var rec) ? rec.AdapterId : "unknown");
            var status = live is null ? "dormant" : live.IsTurnActive ? "working" : "idle";
            var head = await _store.GetHeadAsync(id, cancellationToken).ConfigureAwait(false);
            result.Add(new SessionListEntry(id, adapterId, StateOrNull(id)?.Title, status, head, live?.CurrentModeId));
        }

        return result;
    }

    /// <summary>
    /// The cross-session approvals list (notifications/02 tier 1): for every live session, the
    /// <see cref="PermissionRequestedEvent"/>s in its log that have no matching
    /// <see cref="PermissionResolvedEvent"/> (matched on <c>RequestId</c>) — i.e. still waiting on a human —
    /// unioned across sessions, PLUS every still-Pending external attention request (extensibility/06) when an
    /// attention store is wired, and returned most-recent first. Pure aggregation over durable state; it
    /// creates nothing and mutates nothing. External entries carry a null <c>SessionId</c>, the
    /// <see cref="OpenApprovalKind.ExternalAttention"/> kind, and their caller <c>Source</c> label — the first
    /// five positional fields stay identical to the session-permission shape, so pre-existing consumers are
    /// unaffected.
    /// </summary>
    public async Task<IReadOnlyList<OpenApproval>> GetOpenApprovalsAsync(CancellationToken cancellationToken = default)
    {
        var open = new List<OpenApproval>();
        foreach (var sessionId in _sessions.Keys)
        {
            var events = await _store.ReadSinceAsync(sessionId, 0, cancellationToken).ConfigureAwait(false);
            var resolved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in events)
            {
                if (e is PermissionResolvedEvent r)
                {
                    resolved.Add(r.RequestId);
                }
            }

            foreach (var e in events)
            {
                if (e is PermissionRequestedEvent p && !resolved.Contains(p.RequestId))
                {
                    open.Add(new OpenApproval(sessionId, p.RequestId, p.Title, p.ToolCallId, p.Timestamp));
                }
            }
        }

        foreach (var request in _attention?.ListPending() ?? [])
        {
            open.Add(new OpenApproval(
                SessionId: null,
                RequestId: request.Id,
                Title: request.Question,
                ToolCallId: string.Empty,
                RequestedAt: request.CreatedAt,
                Kind: OpenApprovalKind.ExternalAttention,
                Source: request.Source,
                Options: request.Options));
        }

        // Approval-gated actions (notifications/02 tier 2) awaiting sign-off ride the same inbox: null session
        // (nothing to jump to), the action id as the Source label, the argument summary as the Title, and the
        // GatedAction kind so a consumer renders approve/reject rather than a permission option list.
        foreach (var request in _approvals?.ListOpen() ?? [])
        {
            open.Add(new OpenApproval(
                SessionId: null,
                RequestId: request.Id,
                Title: request.ArgsSummary,
                ToolCallId: string.Empty,
                RequestedAt: request.CreatedAt,
                Kind: OpenApprovalKind.GatedAction,
                Source: request.ActionId,
                Options: request.Preview is { Length: > 0 } p ? [p] : null));
        }

        return open.OrderByDescending(a => a.RequestedAt).ToArray();
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

        foreach (var terminal in _terminals.Values)
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var state in _state.Values)
        {
            if (state.Worktree is { } w)
            {
                await _git.RemoveWorktreeAsync(w.Repo, w.Worktree).ConfigureAwait(false);
            }
        }

        _attachGate.Dispose();
    }
}
