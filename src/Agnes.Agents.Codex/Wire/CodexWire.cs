using System.Text.Json;

namespace Agnes.Agents.Codex.Wire;

// Outbound (client -> server) request/response payloads for the Codex app-server protocol.
// Only the fields Agnes actually sends or reads are modelled; the server tolerates the rest being
// absent. Inbound notification payloads are modelled at the bottom of this file.

internal sealed record CodexInitializeParams(CodexClientInfo ClientInfo);

internal sealed record CodexClientInfo(string Name, string Version);

/// <summary>Result of <c>initialize</c> — we only need it to succeed, so nothing is read from it.</summary>
internal sealed record CodexInitializeResult;

internal sealed record CodexThreadStartParams
{
    public string? Cwd { get; init; }

    /// <summary>"untrusted" | "on-request" | "never" — how Codex decides when to ask for approval.</summary>
    public string? ApprovalPolicy { get; init; }

    /// <summary>"read-only" | "workspace-write" | "danger-full-access".</summary>
    public string? Sandbox { get; init; }
}

internal sealed record CodexThreadStartResult(CodexThread Thread, string? Model);

internal sealed record CodexThread(string Id);

internal sealed record CodexTurnStartParams(string ThreadId, IReadOnlyList<CodexUserInput> Input);

/// <summary>A single input item on a turn. Agnes sends text (and, later, images).</summary>
internal sealed record CodexUserInput(string Type, string Text);

internal sealed record CodexTurnStartResult(CodexTurnRef Turn);

internal sealed record CodexTurnRef(string Id);

internal sealed record CodexTurnInterruptParams(string ThreadId);

/// <summary>Reply to an <c>item/*/requestApproval</c> server request: "approved" or "denied".</summary>
internal sealed record CodexApprovalResponse(string Decision);

/// <summary>Reply to an <c>item/tool/requestUserInput</c> server request: per-question answers keyed by
/// question id (each answer is an array of chosen strings — a single element for single-select).</summary>
internal sealed record CodexRequestUserInputResult(IReadOnlyDictionary<string, CodexUserInputAnswer> Answers);

internal sealed record CodexUserInputAnswer(IReadOnlyList<string> Answers);

// ---- inbound (server -> client) notification payloads ----
// Deserialized once (CodexJson.Read) then matched on the item Type in CodexMap, rather than hand-traversed.
// A few genuinely polymorphic sub-fields stay as JsonElement — that's the one place the shape truly varies
// (e.g. a reasoning "content" that is a string in one message and an array of blocks in the next).

/// <summary>An <c>item/started</c> or <c>item/completed</c> notification envelope.</summary>
internal sealed record CodexItemNotification(CodexItem? Item);

/// <summary>A Codex thread item. Fields are the union across all item kinds (agentMessage, reasoning, plan,
/// commandExecution, fileChange, webSearch, mcpToolCall, imageView, subAgentActivity, …); which are present
/// depends on <see cref="Type"/>. Modelled as one nullable-rich record because Codex's discriminator isn't
/// something we control tightly enough to trust polymorphic deserialization on.</summary>
internal sealed record CodexItem
{
    public string? Id { get; init; }
    public string? Type { get; init; }
    public string? Text { get; init; }
    public string? Status { get; init; }
    public string? Summary { get; init; }
    public string? Query { get; init; }
    public string? Tool { get; init; }
    public string? Path { get; init; }
    public string? AggregatedOutput { get; init; }
    public string? AgentThreadId { get; init; }
    public string? Kind { get; init; }
    public IReadOnlyList<CodexFileChange>? Changes { get; init; }

    /// <summary>Reasoning content: a string in some messages, an array of <c>{ text }</c> blocks in others.</summary>
    public JsonElement? Content { get; init; }

    /// <summary>A command: a string, or an argv array of strings.</summary>
    public JsonElement? Command { get; init; }

    /// <summary>Arbitrary tool arguments, surfaced verbatim (we don't model per-tool schemas).</summary>
    public JsonElement? Arguments { get; init; }
}

internal sealed record CodexFileChange(string? Path);

/// <summary>An <c>item/agentMessage/delta</c> notification: a streamed chunk of assistant text.</summary>
internal sealed record CodexAgentMessageDeltaNotification(string? ItemId, string? Delta);

/// <summary>A <c>thread/tokenUsage/updated</c> notification.</summary>
internal sealed record CodexTokenUsageNotification(CodexTokenUsage? TokenUsage);

/// <summary>Token usage. Totals may arrive nested under <see cref="Total"/> or flattened onto this object;
/// the mapper prefers the nested form and falls back to the top-level fields.</summary>
internal sealed record CodexTokenUsage
{
    public long? ModelContextWindow { get; init; }
    public CodexTokenTotals? Total { get; init; }
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? OutputTokens { get; init; }
}

internal sealed record CodexTokenTotals(long? InputTokens, long? CachedInputTokens, long? OutputTokens);
