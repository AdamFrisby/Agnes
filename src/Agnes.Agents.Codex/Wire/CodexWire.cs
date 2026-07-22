namespace Agnes.Agents.Codex.Wire;

// Outbound (client -> server) request/response payloads for the Codex app-server protocol.
// Only the fields Agnes actually sends or reads are modelled; the server tolerates the rest being
// absent, and inbound notifications are read as JsonElement in CodexMap rather than typed here.

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
