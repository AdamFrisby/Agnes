using System.Text.Json.Serialization;

namespace Agnes.Abstractions;

/// <summary>How a tool call is classified, for iconography and grouping.</summary>
public enum ToolKind
{
    Read,
    Edit,
    Delete,
    Move,
    Search,
    Execute,
    Think,
    Fetch,
    Other,
}

/// <summary>Lifecycle state of a tool call.</summary>
public enum ToolCallStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
}

/// <summary>Why an agent turn stopped.</summary>
public enum StopReason
{
    EndTurn,
    MaxTokens,
    MaxTurnRequests,
    Refusal,
    Cancelled,
}

/// <summary>A single entry in an agent's plan.</summary>
public sealed record PlanEntry(string Content, string Status, string? Priority = null);

/// <summary>
/// The normalized, append-only unit of session history. Every ACP
/// <c>session/update</c> (and PTY fallback output) is mapped to one of these and
/// appended to a session's log with a monotonic <see cref="Sequence"/>. This is the
/// single event model the host, wire protocol, and every frontend all speak.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventKind")]
[JsonDerivedType(typeof(SessionStartedEvent), "session_started")]
[JsonDerivedType(typeof(MessageChunkEvent), "message_chunk")]
[JsonDerivedType(typeof(ThoughtChunkEvent), "thought_chunk")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ToolCallUpdateEvent), "tool_call_update")]
[JsonDerivedType(typeof(PlanEvent), "plan")]
[JsonDerivedType(typeof(ModeChangedEvent), "mode_changed")]
[JsonDerivedType(typeof(PermissionRequestedEvent), "permission_requested")]
[JsonDerivedType(typeof(PermissionResolvedEvent), "permission_resolved")]
[JsonDerivedType(typeof(QuestionAskedEvent), "question_asked")]
[JsonDerivedType(typeof(QuestionAnsweredEvent), "question_answered")]
[JsonDerivedType(typeof(TerminalOutputEvent), "terminal_output")]
[JsonDerivedType(typeof(TurnEndedEvent), "turn_ended")]
[JsonDerivedType(typeof(UsageReportedEvent), "usage_reported")]
[JsonDerivedType(typeof(AgentErrorEvent), "agent_error")]
[JsonDerivedType(typeof(SubagentStartedEvent), "subagent_started")]
[JsonDerivedType(typeof(NoticeEvent), "notice")]
[JsonDerivedType(typeof(McpToolCallEvent), "mcp_tool_call")]
[JsonDerivedType(typeof(GitCredentialEvent), "git_credential")]
[JsonDerivedType(typeof(SessionTitleEvent), "session_title")]
public abstract record SessionEvent : Events.IAgnesEvent
{
    /// <summary>Monotonic, per-session ordering key. Assigned by the host on append.</summary>
    public long Sequence { get; init; }

    /// <summary>When the host recorded the event (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Which agent within the session produced this event: null for the main agent, or a
    /// subagent id (see <see cref="SubagentStartedEvent"/>). Lets the UI group a session's
    /// main + subagent conversations without changing the flat, ordered event log.
    /// </summary>
    public string? AgentId { get; init; }
}

/// <summary>The agent accepted a new session.</summary>
public sealed record SessionStartedEvent(string AgentSessionId) : SessionEvent;

/// <summary>A streamed chunk of a user or assistant message.</summary>
public sealed record MessageChunkEvent(MessageRole Role, ContentBlock Content) : SessionEvent;

/// <summary>A streamed chunk of the agent's reasoning/thinking.</summary>
public sealed record ThoughtChunkEvent(ContentBlock Content) : SessionEvent;

/// <summary>A tool call the agent started.</summary>
public sealed record ToolCallEvent(
    string ToolCallId,
    string Title,
    ToolKind Kind,
    ToolCallStatus Status,
    IReadOnlyList<ContentBlock> Content) : SessionEvent;

/// <summary>An update to a previously reported tool call.</summary>
public sealed record ToolCallUpdateEvent(
    string ToolCallId,
    ToolCallStatus? Status,
    IReadOnlyList<ContentBlock>? Content) : SessionEvent;

/// <summary>The agent's current plan.</summary>
public sealed record PlanEvent(IReadOnlyList<PlanEntry> Entries) : SessionEvent;

/// <summary>The agent's active mode changed.</summary>
public sealed record ModeChangedEvent(string ModeId) : SessionEvent;

/// <summary>The agent is requesting the user's permission to proceed with a tool call.</summary>
public sealed record PermissionRequestedEvent(
    string RequestId,
    string ToolCallId,
    string Title,
    IReadOnlyList<PermissionOption> Options) : SessionEvent;

/// <summary>A pending permission request was resolved (by a client or by cancellation).</summary>
public sealed record PermissionResolvedEvent(
    string RequestId,
    string? OptionId,
    PermissionOutcome Outcome) : SessionEvent;

/// <summary>The agent is asking the user a set of structured design/clarifying questions (AskUserQuestion
/// on Claude, item/tool/requestUserInput on Codex). Answered via the client's structured question card.</summary>
public sealed record QuestionAskedEvent(
    string RequestId,
    string ToolCallId,
    IReadOnlyList<AgentQuestion> Questions) : SessionEvent;

/// <summary>A pending question set was answered (or dismissed) — marks the card resolved.</summary>
public sealed record QuestionAnsweredEvent(string RequestId) : SessionEvent;

/// <summary>Raw output from the CLI-fallback terminal attached to this session.</summary>
public sealed record TerminalOutputEvent(string TerminalId, string Data) : SessionEvent;

/// <summary>An agent turn finished.</summary>
public sealed record TurnEndedEvent(StopReason Reason) : SessionEvent;

/// <summary>
/// Real token/cost usage the agent reported (today: the native Claude Code adapter, from the
/// stream's per-message and result <c>usage</c> blocks). Every field is nullable — a client shows
/// only what's present, and nothing at all when the agent reports no usage. Nothing here is
/// estimated or fabricated: <see cref="ContextTokens"/> is the context-window occupancy the model
/// reported, <see cref="ContextWindow"/> is the model's real window (when known), and
/// <see cref="CostUsd"/> is the cost the CLI reported.
/// </summary>
public sealed record UsageReportedEvent(
    long? ContextTokens = null,
    long? ContextWindow = null,
    long? OutputTokens = null,
    double? CostUsd = null) : SessionEvent;

/// <summary>A host-level informational notice in the transcript (e.g. a session was reconnected).</summary>
public sealed record NoticeEvent(string Message, bool IsError = false) : SessionEvent;

/// <summary>The agent's auto-generated title/summary for the conversation (e.g. Claude's on-disk
/// <c>aiTitle</c>), surfaced so clients can name the session instead of using the folder name.</summary>
public sealed record SessionTitleEvent(string Title) : SessionEvent;

/// <summary>
/// A tool call the host observed a sandboxed agent make against a <b>forwarded</b> host MCP server
/// (Agnes is in the JSON-RPC path, so it can record it). Audit-only: the client shows it in an MCP
/// trail; it does not gate the call (the agent's own permission protocol does that).
/// </summary>
public sealed record McpToolCallEvent(string Server, string Tool) : SessionEvent;

/// <summary>
/// A sandboxed agent obtained (or was denied) a brokered git credential — the audit trail for the
/// credential broker. <see cref="Allowed"/> is false when the request was out of scope or the user
/// declined the permission card.
/// </summary>
public sealed record GitCredentialEvent(string Host, string? Repo, bool Allowed) : SessionEvent;

/// <summary>The agent (or adapter) reported an error.</summary>
public sealed record AgentErrorEvent(string Message) : SessionEvent;

/// <summary>
/// The main agent spawned a subagent (e.g. via a Task/delegation tool). Subsequent events
/// carrying this <see cref="AgentId"/> belong to the subagent's sub-conversation.
/// </summary>
public sealed record SubagentStartedEvent(string SubagentId, string Name, string? ParentAgentId = null) : SessionEvent;
