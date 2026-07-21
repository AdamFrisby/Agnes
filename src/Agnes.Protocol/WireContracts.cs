using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Protocol;

/// <summary>Identity of a host a client can connect to.</summary>
public sealed record HostInfo(string HostId, string DisplayName, string Version);

/// <summary>Request to pair a new device using the host's current pairing code.</summary>
public sealed record PairRequest(string Code, string DeviceName);

/// <summary>A successful pairing — the per-device token to store and connect with (shown once).</summary>
public sealed record PairResponse(string DeviceId, string DeviceName, string Token);

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
public sealed record DeviceInfo(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt);

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
    string McpApproval = "Ask", string GitCredentialMode = "Off");

/// <summary>Stores a token credential source for a host (the low-setup fine-grained-PAT fallback).</summary>
public sealed record StoreCredentialRequest(string Host, string Token, string? Username = null);

/// <summary>The host's credential-linking state (GitHub App): not-connected / app-created / connected.</summary>
public sealed record CredentialStatus(string State, string? Slug, bool Installed, string? Account);

/// <summary>Request to send a prompt to a session.</summary>
public sealed record PromptRequest(string SessionId, IReadOnlyList<ContentBlock> Content);

/// <summary>A client's answer to a permission request.</summary>
public sealed record PermissionResponseRequest(string SessionId, string RequestId, string OptionId);

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
