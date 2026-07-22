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
/// device flow — never a secret.</summary>
public sealed record AuthMethods(bool Pairing, bool GitHub, string? GitHubClientId, bool Keypair);

/// <summary>Exchange a GitHub user access token (obtained by the client via the device flow) for an Agnes
/// device token. The host verifies the identity against its allowlist and discards the GitHub token.</summary>
public sealed record GitHubExchangeRequest(string Token, string DeviceName);

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
public sealed record UsageInfo(
    long? ContextUsed = null,
    long? ContextWindow = null,
    long? OutputTokens = null,
    double? CostUsd = null)
{
    /// <summary>The model reported a context-token count (so we can show at least the number).</summary>
    [JsonIgnore] public bool HasAnyContext => ContextUsed is >= 0;

    /// <summary>We know both the used tokens and the model's window (so we can show a meter).</summary>
    [JsonIgnore] public bool HasContext => ContextWindow is > 0 && ContextUsed is >= 0;

    [JsonIgnore] public double ContextPercent => HasContext ? Math.Clamp(100.0 * ContextUsed!.Value / ContextWindow!.Value, 0, 100) : 0;

    /// <summary>"18,240 / 200,000" when the window is known, else just "18,240", else empty.</summary>
    [JsonIgnore] public string ContextText => HasContext
        ? $"{ContextUsed:N0} / {ContextWindow:N0}"
        : HasAnyContext ? $"{ContextUsed:N0}" : string.Empty;

    /// <summary>A compact status caption (the real reported cost), or null when there's nothing to show.</summary>
    [JsonIgnore] public string? Summary => CostUsd is > 0 ? $"${CostUsd:0.####}" : null;
}

/// <summary>An agent kind available on a host (a loaded adapter plugin).</summary>
public sealed record AgentInfo(
    string AdapterId,
    string DisplayName,
    string? Version,
    bool Available);

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
/// An MCP server registered on a host. <see cref="RunAt"/> is "host" (runs on the Agnes host; used
/// by host sessions and forwarded into sandboxes) or "sandbox" (runs inside the VM). <see cref="Transport"/>
/// is "stdio" (Command/Args/Env) or "http" (Url/BearerTokenEnv). A server is used only when Enabled.
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
    string? BearerTokenEnv);

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
    string? BearerTokenEnv = null);

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
public sealed record OpenSessionRequest(
    string AdapterId, string WorkingDirectory, bool UseWorktree = false, bool SkipPermissions = false,
    string McpApproval = "Ask", string GitCredentialMode = "Off", bool UseSandbox = true);

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

/// <summary>Stores a token credential source for a host (the low-setup fine-grained-PAT fallback).</summary>
public sealed record StoreCredentialRequest(string Host, string Token, string? Username = null);

/// <summary>The host's credential-linking state (GitHub App): not-connected / app-created / connected.</summary>
public sealed record CredentialStatus(string State, string? Slug, bool Installed, string? Account);

/// <summary>Request to send a prompt to a session.</summary>
public sealed record PromptRequest(string SessionId, IReadOnlyList<ContentBlock> Content);

/// <summary>A client's answer to a permission request.</summary>
public sealed record PermissionResponseRequest(string SessionId, string RequestId, string OptionId);

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

/// <summary>A recurring background task: run <see cref="Prompt"/> on an interval.</summary>
public sealed record ScheduledTask(
    string Id,
    string AdapterId,
    string WorkingDirectory,
    string Prompt,
    int IntervalSeconds,
    bool Enabled);

/// <summary>A request to schedule a recurring task.</summary>
public sealed record ScheduleTaskRequest(
    string AdapterId,
    string WorkingDirectory,
    string Prompt,
    int IntervalSeconds);

/// <summary>A completed background run, collected in the inbox.</summary>
public sealed record InboxRun(
    string Id,
    string TaskId,
    string Title,
    string Summary,
    DateTimeOffset CompletedAt);
