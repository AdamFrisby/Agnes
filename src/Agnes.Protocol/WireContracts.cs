using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Protocol;

/// <summary>Identity of a host a client can connect to.</summary>
public sealed record HostInfo(string HostId, string DisplayName, string Version);

/// <summary>
/// Structured usage a host may report for a session: context-window consumption and/or
/// token/credit usage against a quota. Any field may be null when unknown; <see cref="Label"/>
/// is a free-form fallback caption. (Real hosts will populate this via a future ACP extension;
/// today only the simulator does.)
/// </summary>
public sealed record UsageInfo(
    long? ContextUsed = null,
    long? ContextMax = null,
    long? Used = null,
    long? Limit = null,
    string? Label = null)
{
    [JsonIgnore] public bool HasContext => ContextMax is > 0 && ContextUsed is >= 0;
    [JsonIgnore] public bool HasQuota => Limit is > 0 && Used is >= 0;

    [JsonIgnore] public double ContextPercent => HasContext ? Math.Clamp(100.0 * ContextUsed!.Value / ContextMax!.Value, 0, 100) : 0;
    [JsonIgnore] public double QuotaPercent => HasQuota ? Math.Clamp(100.0 * Used!.Value / Limit!.Value, 0, 100) : 0;

    [JsonIgnore] public string ContextText => HasContext ? $"{ContextUsed:N0} / {ContextMax:N0} ctx" : string.Empty;
    [JsonIgnore] public string QuotaText => HasQuota ? $"{Used:N0} / {Limit:N0}" : string.Empty;
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
    SandboxStatus? Sandbox = null);

/// <summary>Status of the sandbox a session runs in, or null if it runs on the host.</summary>
public sealed record SandboxStatus(string Provider, string Id, string State);

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
    string AdapterId, string WorkingDirectory, bool UseWorktree = false, bool SkipPermissions = false);

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
