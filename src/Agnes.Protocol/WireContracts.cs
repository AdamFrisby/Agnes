using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Protocol;

/// <summary>Identity of a host a client can connect to. <see cref="SandboxAvailable"/> tells the client
/// whether the host can isolate sessions in per-session VMs (so the new-session screen can default it on).</summary>
public sealed record HostInfo(string HostId, string DisplayName, string Version, bool SandboxAvailable = false);

/// <summary>Request to pair a new device using the host's current pairing code.</summary>
public sealed record PairRequest(string Code, string DeviceName);

/// <summary>A successful pairing — the per-device token to store and connect with (shown once).
/// Shared by every bootstrap method (pairing code, GitHub SSO, keypair).</summary>
public sealed record PairResponse(string DeviceId, string DeviceName, string Token);

/// <summary>Which bootstrap auth methods a host offers (advertised at <c>GET /auth/methods</c>) so a
/// client shows only the enabled ones. <see cref="GitHubClientId"/> is a public OAuth client id for the
/// device flow — never a secret. The enterprise methods (<see cref="Oidc"/>, <see cref="Mtls"/>) are
/// trailing-optional so this stays wire-compatible with clients that predate them.</summary>
public sealed record AuthMethods(
    bool Pairing,
    bool GitHub,
    string? GitHubClientId,
    bool Keypair,
    bool Oidc = false,
    string? OidcIssuer = null,
    bool Mtls = false);

/// <summary>Exchange a GitHub user access token (obtained by the client via the device flow) for an Agnes
/// device token. The host verifies the identity against its allowlist and discards the GitHub token.</summary>
public sealed record GitHubExchangeRequest(string Token, string DeviceName);

/// <summary>Exchange an OIDC-issued token (validated against the configured issuer's JWKS/audience) for an
/// Agnes device token. The OIDC token is verified then discarded.</summary>
public sealed record OidcExchangeRequest(string Token, string DeviceName);

/// <summary>Complete mTLS pairing once a valid client certificate has been presented on the TLS
/// connection; the certificate is the credential, so the body carries only a device name.</summary>
public sealed record MtlsPairRequest(string DeviceName);

/// <summary>A one-time challenge nonce for keypair auth (from <c>GET /auth/keypair/challenge</c>).</summary>
public sealed record KeypairChallenge(string Nonce);

/// <summary>Prove possession of an authorized key: the base64 SPKI public key, the challenge nonce, and
/// the P-256/SHA-256 signature over the nonce bytes (base64), plus a device name.</summary>
public sealed record KeypairAuthRequest(string PublicKey, string Nonce, string Signature, string DeviceName);

/// <summary>
/// Structured usage a host may report for a session: context-window consumption and/or
/// token/credit usage against a quota. Any field may be null when unknown; <see cref="Label"/>
/// is a free-form fallback caption. (Real hosts will populate this via a future ACP extension;
/// today only the simulator does.)
/// </summary>
/// Presentation over the shared <see cref="UsageMetrics"/> — the same data the <c>UsageReportedEvent</c>
/// carries, with derived display helpers. There is one data shape (<see cref="UsageMetrics"/>); this only
/// adds computed captions the UI binds to. A convenience constructor keeps the flat call sites terse.
public sealed record UsageInfo(UsageMetrics Metrics)
{
    public UsageInfo(long? ContextUsed = null, long? ContextWindow = null, long? OutputTokens = null, double? CostUsd = null)
        : this(new UsageMetrics(ContextUsed, ContextWindow, OutputTokens, CostUsd)) { }

    /// <summary>The model reported a context-token count (so we can show at least the number).</summary>
    [JsonIgnore] public bool HasAnyContext => Metrics.ContextUsed is >= 0;

    /// <summary>We know both the used tokens and the model's window (so we can show a meter).</summary>
    [JsonIgnore] public bool HasContext => Metrics.ContextWindow is > 0 && Metrics.ContextUsed is >= 0;

    [JsonIgnore] public double ContextPercent => HasContext ? Math.Clamp(100.0 * Metrics.ContextUsed!.Value / Metrics.ContextWindow!.Value, 0, 100) : 0;

    /// <summary>"18,240 / 200,000" when the window is known, else just "18,240", else empty.</summary>
    [JsonIgnore] public string ContextText => HasContext
        ? $"{Metrics.ContextUsed:N0} / {Metrics.ContextWindow:N0}"
        : HasAnyContext ? $"{Metrics.ContextUsed:N0}" : string.Empty;

    /// <summary>A compact status caption (the real reported cost), or null when there's nothing to show.</summary>
    [JsonIgnore] public string? Summary => Metrics.CostUsd is > 0 ? $"${Metrics.CostUsd:0.####}" : null;
}

/// <summary>An agent kind available on a host (a loaded adapter plugin). <see cref="Auth"/> is the CLI's
/// machine-local login state when the adapter reports one, or null when it has no reliable signal — in which
/// case the picker shows no auth badge (only the installed/not-installed <see cref="Available"/> signal).</summary>
public sealed record AgentInfo(
    string AdapterId,
    string DisplayName,
    string? Version,
    bool Available,
    ProviderAuthStatus? Auth = null);

/// <summary>
/// Whether one plugin-point id is populated on this host, from <c>GetCapabilities()</c>. Lets a
/// client learn "no voice provider configured" up front instead of discovering it via a failed
/// call. <see cref="FailClosed"/> tells the client how to treat an unavailable capability: a
/// fail-closed capability should block/hide the dependent action outright, a fail-open one should
/// let the action proceed and degrade gracefully (e.g. a session just runs unsandboxed).
/// </summary>
public sealed record HostCapability(string Id, bool Available, bool FailClosed);

/// <summary>Stable ids for the host-level capabilities <see cref="HostCapability"/> reports.</summary>
public static class HostCapabilityIds
{
    /// <summary>At least one <c>IAgentAdapter</c> is registered — without this, no session can open.</summary>
    public const string AgentAdapter = "agent-adapter";

    /// <summary>An <c>ISandboxProvider</c> is configured. Absence degrades gracefully: sessions just
    /// run on the host instead of in a per-session VM.</summary>
    public const string SandboxProvider = "sandbox-provider";

    /// <summary>The NuGet-packaged plugin lifecycle (search/install/enable/…) is available on this
    /// host. Absence degrades gracefully: a client just hides the Plugins screen.</summary>
    public const string PluginManagement = "plugin-management";

    /// <summary>An <c>IMemoryIndexProvider</c> is configured, so transcript search is available. Absence
    /// degrades gracefully: a client hides the search screen (a search still returns an empty list).</summary>
    public const string MemorySearch = "memory-search";
}

/// <summary>
/// What a connecting client can do, sent to the host so it can reconcile against its own capabilities
/// (see <c>.ideas/00c-client-plugins-and-negotiation.md</c>). <see cref="SupportsDynamicPlugins"/> is
/// false on locked-down heads (iOS/WASM) that can only run compile-time plugins.
/// </summary>
public sealed record ClientCapabilities(
    string ClientId,
    string Platform,
    bool SupportsDynamicPlugins,
    IReadOnlyList<string> PluginPointIds,
    IReadOnlyList<string> CapabilityIds);

/// <summary>Where a capability id is supported across the two parties.</summary>
public enum CapabilitySupport
{
    /// <summary>Only the host has it (e.g. sandboxing) — usable regardless of the client.</summary>
    HostOnly,

    /// <summary>Only the client has it (e.g. a custom tool renderer) — no host dependency.</summary>
    ClientOnly,

    /// <summary>Both parties have it — the only state in which a two-sided feature is usable end to end.</summary>
    Both,
}

/// <summary>One capability id and how it lines up across client and host.</summary>
public sealed record NegotiatedCapability(string Id, CapabilitySupport Support, bool FailClosed);

/// <summary>The reconciled view returned to the client after it advertises its capabilities.</summary>
public sealed record NegotiatedCapabilities(IReadOnlyList<NegotiatedCapability> Capabilities);

/// <summary>Stable ids for capabilities a client advertises to the host. Two-sided feature ids (like
/// <see cref="Notifications"/>) are neutral and shared: both parties advertise the same id, so the
/// reconciliation reports <see cref="CapabilitySupport.Both"/> only when each side has its half.</summary>
public static class ClientCapabilityIds
{
    /// <summary>End-to-end notifications: the client advertises this when it has a notification channel
    /// (can show one on its device); the host advertises the same id when it can trigger them. Only when
    /// both do is it <see cref="CapabilitySupport.Both"/>.</summary>
    public const string Notifications = "notifications";

    /// <summary>Client-only voice control: advertised when the client has at least one registered
    /// <c>IVoiceProvider</c>. The controller drives existing host calls, so there is no host half — voice UI
    /// is simply hidden on clients that don't advertise it (see <c>.ideas/voice/01-voice-assistant.md</c>).</summary>
    public const string Voice = "voice";
}

/// <summary>A plugin package a Browse/search returned — the wire shape of
/// <c>Agnes.Abstractions.PluginSearchResult</c>.</summary>
public sealed record PluginSearchResultDto(
    string PackageId, string DisplayName, string? Description, string Publisher,
    IReadOnlyList<string> Versions, bool IsReviewed);

/// <summary>An installed plugin as the client sees it — the wire shape of
/// <c>Agnes.Abstractions.InstalledPlugin</c>.</summary>
public sealed record InstalledPluginDto(
    string PluginId, string Version, bool Enabled,
    IReadOnlyList<string> GrantedCapabilities, bool UpdateAvailable);

/// <summary>Install (or update) a plugin. <see cref="GrantedCapabilities"/> is the set of the package's
/// declared capabilities the user consented to — the host refuses if the package declares one this list
/// doesn't cover (the client is expected to have shown a consent prompt first).</summary>
public sealed record InstallPluginRequest(string PackageId, string? Version, IReadOnlyList<string> GrantedCapabilities);

/// <summary>The typed result of an install/update attempt. On <see cref="ConsentRequired"/>, the host
/// refused because <see cref="MissingCapabilities"/> weren't granted — the client shows a consent prompt
/// and retries with them included, rather than this being surfaced as a raw exception.</summary>
public sealed record PluginInstallOutcome(
    bool Success, InstalledPluginDto? Plugin, bool ConsentRequired,
    IReadOnlyList<string> MissingCapabilities, string? Error);

/// <summary>Metadata about a live or resumable session.</summary>
public sealed record SessionInfo(
    string SessionId,
    string AdapterId,
    string WorkingDirectory,
    long HeadSequence,
    IReadOnlyList<SessionMode>? Modes = null,
    string? CurrentModeId = null,
    SandboxStatus? Sandbox = null,
    bool SkipPermissions = false,
    string? Project = null);

/// <summary>The per-session defaults a project suggests.</summary>
public sealed record ProjectDefaultsDto(bool SkipPermissions = false, string GitCredentialMode = "Ask", string McpApproval = "Ask");

/// <summary>
/// A project as the client sees it: the per-repo bundle of sandbox contents, MCP servers, GitHub
/// account and defaults that its sessions inherit. RepoKey "" is the default/fallback project.
/// </summary>
public sealed record ProjectDto(
    string Id,
    string Name,
    string RepoKey,
    SandboxImageDto Sandbox,
    IReadOnlyList<McpServerInfo> McpServers,
    string? CredentialAccount,
    ProjectDefaultsDto Defaults,
    string? Repo = null);

/// <summary>A device paired with a host (metadata only — never the token).</summary>
public sealed record DeviceInfo(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, string? Subject = null);

/// <summary>
/// How widely an MCP server applies, resolved at session start. <see cref="AllHosts"/> and
/// <see cref="ThisHost"/> both apply on the host that stores them (they differ only in a multi-host
/// client's configured view — a per-host registry can't see other hosts); <see cref="ThisWorkspace"/>
/// applies only to the workspace named by <see cref="McpServerInfo.WorkspaceId"/>. The zero value is
/// <see cref="AllHosts"/> so an entry persisted before scopes existed deserializes to "always applies".
/// </summary>
public enum McpApplyScope
{
    /// <summary>Applies everywhere (the back-compatible default for entries with no recorded scope).</summary>
    AllHosts,

    /// <summary>Applies to this host only.</summary>
    ThisHost,

    /// <summary>Applies only to the one workspace named by <see cref="McpServerInfo.WorkspaceId"/>.</summary>
    ThisWorkspace,
}

/// <summary>
/// An MCP server registered on a host. <see cref="RunAt"/> is "host" (runs on the Agnes host; used
/// by host sessions and forwarded into sandboxes) or "sandbox" (runs inside the VM). <see cref="Transport"/>
/// is "stdio" (Command/Args/Env) or "http" (Url/BearerTokenEnv). A server is used only when Enabled.
/// <see cref="ApplyScope"/> (with <see cref="WorkspaceId"/> for <see cref="McpApplyScope.ThisWorkspace"/>)
/// narrows which sessions see it; both are additive and default to "applies everywhere" for back-compat.
/// </summary>
public sealed record McpServerInfo(
    string Id,
    string Name,
    string RunAt,
    bool Enabled,
    string Transport,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url,
    string? BearerTokenEnv,
    McpApplyScope ApplyScope = McpApplyScope.AllHosts,
    string? WorkspaceId = null);

/// <summary>Create/replace payload for an MCP server (Id is assigned by the host on add).</summary>
public sealed record McpServerRequest(
    string Name,
    string RunAt,
    bool Enabled,
    string Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Url = null,
    string? BearerTokenEnv = null,
    McpApplyScope ApplyScope = McpApplyScope.AllHosts,
    string? WorkspaceId = null);

/// <summary>Status of the sandbox a session runs in, or null if it runs on the host.</summary>
public sealed record SandboxStatus(string Provider, string Id, string State);

/// <summary>The baked-sandbox-image manifest, over the wire (mirrors host SandboxImageManifest).</summary>
public sealed record SandboxImageDto(
    string BaseImage,
    string Alias,
    bool Node,
    IReadOnlyList<string> AptPackages,
    IReadOnlyList<string> NpmGlobals,
    IReadOnlyList<string> PipPackages,
    IReadOnlyList<SandboxImageAgentDto> Agents);

/// <summary>An agent CLI in a baked image: Source is "copy:&lt;hostBinary&gt;" or "npm:&lt;package&gt;".</summary>
public sealed record SandboxImageAgentDto(string AdapterId, string Source);

/// <summary>Bake status: State is "absent" | "building" | "ready" | "failed".</summary>
public sealed record SandboxImageStatusDto(string State, string Message, DateTimeOffset? UpdatedAt);

/// <summary>A managed sandbox VM in the Settings › Sandboxes list. State is "running" | "stopped";
/// Live means its session is currently open/attached in the daemon.</summary>
public sealed record SandboxRecordDto(
    string SessionId,
    string VmName,
    string Provider,
    string AdapterId,
    string WorkingDirectory,
    string? ProjectName,
    string Title,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    bool Live);

/// <summary>The manifest plus its current bake status.</summary>
public sealed record SandboxImageView(SandboxImageDto Manifest, SandboxImageStatusDto Status);

/// <summary>A point-in-time replay: all events up to <see cref="HeadSequence"/>.</summary>
public sealed record SessionSnapshot(
    SessionInfo Session,
    IReadOnlyList<SessionEvent> Events,
    long HeadSequence);

/// <summary>Request to open a new session against an adapter.</summary>
/// <param name="SkipPermissions">
/// Opt into autonomous operation — the agent runs tool calls without asking. Default false: the
/// agent asks the user to approve each tool call (Agnes's intended interactive behaviour).
/// </param>
/// <param name="ModelId">The model the agent's CLI should use, or null for its default. Trailing-optional so
/// pre-model callers keep compiling.</param>
public sealed record OpenSessionRequest(
    string AdapterId, string WorkingDirectory, bool UseWorktree = false, bool SkipPermissions = false,
    string McpApproval = "Ask", string GitCredentialMode = "Off", bool UseSandbox = true, string? ModelId = null);

/// <summary>What a fork would do, computed host-side: the source's working folder, a proposed
/// (non-existing, numeral-incremented) target the UI prefills, and whether the source's sandbox can be
/// copy-on-write cloned. Returned by <c>ProposeFork</c> so the client (which is remote from the host's
/// filesystem) can present an editable target + a "copy sandbox" choice.</summary>
public sealed record ForkPlan(string SourceSessionId, string SourceDirectory, string ProposedDirectory, bool CanCopySandbox);

/// <summary>Fork a session by copying its working folder to <see cref="TargetDirectory"/> and opening a
/// new session there (inheriting the source's agent + options). When <see cref="CopySandbox"/> and the
/// source is sandboxed on a cloner-capable provider, the VM is CoW-cloned; otherwise a fresh sandbox is
/// provisioned (or none, if the source ran on the host).</summary>
public sealed record ForkSessionRequest(string SourceSessionId, string TargetDirectory, bool CopySandbox = true);

/// <summary>Replay-fork request: branch the conversation at <see cref="AtSequence"/> (a parent log
/// sequence), copying the workspace like a plain fork.</summary>
public sealed record ForkAtRequest(string SourceSessionId, string TargetDirectory, long AtSequence, bool CopySandbox = true);

/// <summary>The result of a replay-fork: the new child session, plus the origin user-message text as an
/// editable composer draft when the fork point was a user message (else null).</summary>
public sealed record ForkAtResult(SessionInfo Info, string? Draft);

/// <summary>Stores a token credential source for a host (the low-setup fine-grained-PAT fallback).</summary>
public sealed record StoreCredentialRequest(string Host, string Token, string? Username = null);

/// <summary>The host's credential-linking state (GitHub App): not-connected / app-created / connected.</summary>
public sealed record CredentialStatus(string State, string? Slug, bool Installed, string? Account);

/// <summary>Request to send a prompt to a session.</summary>
public sealed record PromptRequest(string SessionId, IReadOnlyList<ContentBlock> Content);

/// <summary>
/// Request to open a CLI-fallback terminal (real PTY) in a session — the client-facing surface over
/// <see cref="Agnes.Abstractions.ICliFallback"/> (platform/03). Every field is optional/trailing: a null
/// <see cref="Command"/> means the host picks the session's default shell, and a null
/// <see cref="WorkingDirectory"/> means the session's own working directory. Terminal <i>output</i> needs no
/// field here — it rides the session event stream as <see cref="Agnes.Abstractions.TerminalOutputEvent"/>s;
/// only <i>input</i> (keystrokes/paste) crosses back, as raw <c>byte[]</c> via <c>WriteTerminal</c>.
/// </summary>
public sealed record OpenTerminalRequest(
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    int Columns = 120,
    int Rows = 30);

/// <summary>Wire form of <see cref="Agnes.Abstractions.MemorySearchOptions"/> — how many hits to return
/// and an optional single-session scope. The result type (<see cref="Agnes.Abstractions.MemorySearchResult"/>)
/// is already a flat, wire-safe record, so it crosses the boundary unchanged.</summary>
public sealed record MemorySearchOptionsDto(int Limit = 50, string? SessionId = null);

/// <summary>A client's answer to a permission request.</summary>
public sealed record PermissionResponseRequest(string SessionId, string RequestId, string OptionId);

/// <summary>A device's per-trigger push toggles plus the master on/off — the wire twin of the host-side
/// preferences. Each trigger is independently controllable (see <c>.ideas/notifications/01-push-notifications.md</c>).</summary>
public sealed record PushNotificationPrefs(
    bool Enabled = true,
    bool TurnReady = true,
    bool PermissionRequest = true,
    bool UserActionRequest = true);

/// <summary>A device registering (or re-registering) its push token against a notification channel, together
/// with its toggles. <see cref="ChannelId"/> is the target <c>INotificationChannel</c> ("mobile-push",
/// "desktop"); <see cref="ChannelToken"/> is the channel-specific token (an FCM/APNs token for mobile-push).</summary>
public sealed record RegisterPushRequest(string ChannelId, string ChannelToken, PushNotificationPrefs Prefs);

/// <summary>The user's answers to a <see cref="Agnes.Abstractions.QuestionAskedEvent"/> — one entry per
/// question (its id, the chosen option label(s), and optional free-text notes). Empty answers = dismissed.</summary>
public sealed record QuestionAnswerRequest(string SessionId, string RequestId, IReadOnlyList<QuestionAnswerDto> Answers);

public sealed record QuestionAnswerDto(string QuestionId, IReadOnlyList<string> SelectedLabels, string? Notes = null);

/// <summary>Git state of a session's working directory.</summary>
public sealed record GitStatus(
    bool IsRepository,
    string? Branch,
    bool IsDirty,
    IReadOnlyList<GitFileChange> Changes);

/// <summary>One changed file in a git working tree (Status = "M"/"A"/"D"/"??"/…).</summary>
public sealed record GitFileChange(string Path, string Status);

/// <summary>Result of a commit attempt.</summary>
public sealed record GitCommitResult(bool Success, string Message);

/// <summary>Metadata identifying a stash created from a session's working tree, with enough to find it
/// again later. <see cref="StashId"/> is the stash commit sha (stable across list reshuffles).</summary>
public sealed record GitStashInfo(string StashId, string Branch, DateTimeOffset CreatedAt, int FileCount);

/// <summary>
/// Result of a fast-forward-only pull. <see cref="NonFastForward"/> means the remote had diverged and the
/// pull was refused server-side (in <c>GitService</c>) rather than silently merging or rebasing — the
/// safety rule lives at the API layer, not the UI.
/// </summary>
public sealed record GitPullResult(bool Success, bool NonFastForward, string Message);

/// <summary>
/// Result of a branch switch. <see cref="StashReapplyConflict"/> means the switch itself succeeded but a
/// carried stash couldn't be reapplied cleanly; the changes are preserved in stash <see cref="StashId"/>
/// (no data loss) for the user to resolve manually.
/// </summary>
public sealed record GitSwitchResult(bool Success, bool StashReapplyConflict, string? StashId, string Message);

/// <summary>Generic success/message for a git mutation with no richer typed result (stash pop, push,
/// PR checkout).</summary>
public sealed record GitOperationResult(bool Success, string Message);

/// <summary>A request to leave a review comment on a project's file at a specific line.</summary>
public sealed record AddReviewCommentRequest(string ProjectId, string FilePath, int LineNumber, string LineHash, string Text);

/// <summary>
/// A recurring background task: run <see cref="Prompt"/> on a schedule. <see cref="Kind"/> selects the
/// trigger — <c>interval</c> (every <see cref="IntervalSeconds"/>) or <c>cron</c> (<see cref="CronExpression"/>
/// evaluated in <see cref="Timezone"/>). <see cref="TargetKind"/> chooses whether a run opens a new session
/// (<c>new</c>) or prompts an existing live one (<c>existing</c>, identified by <see cref="TargetSessionId"/>).
/// The trailing fields are optional so pre-cron callers keep compiling unchanged.
/// </summary>
public sealed record ScheduledTask(
    string Id,
    string AdapterId,
    string WorkingDirectory,
    string Prompt,
    int IntervalSeconds,
    bool Enabled,
    string Kind = "interval",
    string? CronExpression = null,
    string? Timezone = null,
    string TargetKind = "new",
    string? TargetSessionId = null);

/// <summary>A request to schedule a recurring task (see <see cref="ScheduledTask"/> for the field meanings).</summary>
public sealed record ScheduleTaskRequest(
    string AdapterId,
    string WorkingDirectory,
    string Prompt,
    int IntervalSeconds,
    string Kind = "interval",
    string? CronExpression = null,
    string? Timezone = null,
    string TargetKind = "new",
    string? TargetSessionId = null);

/// <summary>A completed background run, collected in the inbox.</summary>
public sealed record InboxRun(
    string Id,
    string TaskId,
    string Title,
    string Summary,
    DateTimeOffset CompletedAt);

/// <summary>What produced an open approval: an in-session agent permission request, or an external
/// attention request created over the public REST API (extensibility/06). Lets one inbox carry both.</summary>
public enum OpenApprovalKind
{
    /// <summary>An agent tool-call permission request originating inside an Agnes session.</summary>
    SessionPermission,

    /// <summary>An external system's "ask a human" request created over <c>/v1/attention-requests</c>.</summary>
    ExternalAttention,
}

/// <summary>An open request still waiting on a human, surfaced in the cross-session approvals list
/// (notifications/02 tier 1). Aggregated from a session's <c>PermissionRequestedEvent</c>s that have no
/// matching <c>PermissionResolvedEvent</c> — and, additively, from Pending external attention requests
/// (extensibility/06). The trailing <paramref name="Kind"/>/<paramref name="Source"/>/<paramref name="Options"/>
/// fields are optional with back-compatible defaults, so an existing consumer that only reads the first five
/// (and treats every entry as a session permission) is unaffected. For an external request
/// <paramref name="SessionId"/> is null (there is no session to jump to), <paramref name="Kind"/> is
/// <see cref="OpenApprovalKind.ExternalAttention"/>, and <paramref name="Source"/> labels the caller.</summary>
public sealed record OpenApproval(
    string? SessionId,
    string RequestId,
    string Title,
    string ToolCallId,
    DateTimeOffset RequestedAt,
    OpenApprovalKind Kind = OpenApprovalKind.SessionPermission,
    string? Source = null,
    IReadOnlyList<string>? Options = null);

/// <summary>A human's answer to an external attention request, sent from any Agnes client. Answered by
/// request id alone (there is no session) with the chosen option text.</summary>
public sealed record AttentionAnswerRequest(string RequestId, string Answer);

/// <summary>A user-authored bug report sent from a client. Deliberately has NO diagnostic-payload field:
/// the owner-only host-log attachment is deferred, so the client never sends one and the host maps this to a
/// domain <c>BugReport</c> with a null payload. The typed result is
/// <see cref="Agnes.Abstractions.BugReportResult"/> (a created URL, a browser-fallback URL, and/or likely
/// duplicates).</summary>
public sealed record BugReportDto(string Title, string Summary, string? CurrentBehavior, string? ExpectedBehavior);
