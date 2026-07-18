using Agnes.Abstractions;

namespace Agnes.Protocol;

/// <summary>Identity of a host a client can connect to.</summary>
public sealed record HostInfo(string HostId, string DisplayName, string Version);

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
    long HeadSequence);

/// <summary>A point-in-time replay: all events up to <see cref="HeadSequence"/>.</summary>
public sealed record SessionSnapshot(
    SessionInfo Session,
    IReadOnlyList<SessionEvent> Events,
    long HeadSequence);

/// <summary>Request to open a new session against an adapter.</summary>
public sealed record OpenSessionRequest(string AdapterId, string WorkingDirectory);

/// <summary>Request to send a prompt to a session.</summary>
public sealed record PromptRequest(string SessionId, IReadOnlyList<ContentBlock> Content);

/// <summary>A client's answer to a permission request.</summary>
public sealed record PermissionResponseRequest(string SessionId, string RequestId, string OptionId);
