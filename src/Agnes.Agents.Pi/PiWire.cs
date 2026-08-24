using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Agents.Pi;

// Hand-modeled subset of Pi's RPC protocol (pi --mode rpc, verified against v0.84.3). Only what Agnes
// actually reads is modeled; everything is deserialized into these records at the boundary and never
// flows inward as JSON. Pi's own docs for the format ship with the package at docs/rpc.md.

/// <summary>The shape shared by every line Pi writes: a <c>type</c> discriminator. Read first, so the rest
/// of the line is only deserialized once we know what it is.</summary>
internal sealed record PiLine
{
    [JsonPropertyName("type")] public string? Type { get; init; }
}

// ---- command responses ----

/// <summary>A reply to a command Agnes sent (<c>{"type":"response", …}</c>).</summary>
internal sealed record PiResponse
{
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("command")] public string? Command { get; init; }

    [JsonPropertyName("success")] public bool Success { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonPropertyName("data")] public PiResponseData? Data { get; init; }
}

/// <summary>The <c>data</c> payload of the responses Agnes asks for. One record covers both because the
/// fields are disjoint and optional — modelling a variant per command would buy nothing here.</summary>
internal sealed record PiResponseData
{
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }

    [JsonPropertyName("sessionName")] public string? SessionName { get; init; }

    [JsonPropertyName("models")] public IReadOnlyList<PiModel>? Models { get; init; }
}

internal sealed record PiModel
{
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("provider")] public string? Provider { get; init; }
}

// ---- events ----

/// <summary>A streaming delta on an assistant message.</summary>
internal sealed record PiMessageUpdate
{
    [JsonPropertyName("assistantMessageEvent")] public PiAssistantDelta? AssistantMessageEvent { get; init; }
}

internal sealed record PiAssistantDelta
{
    [JsonPropertyName("type")] public string? Type { get; init; }

    [JsonPropertyName("delta")] public string? Delta { get; init; }

    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("toolName")] public string? ToolName { get; init; }
}

/// <summary>A completed message (<c>message_end</c>) — the authoritative form, per Pi's docs.</summary>
internal sealed record PiMessageEnvelope
{
    [JsonPropertyName("message")] public PiMessage? Message { get; init; }
}

internal sealed record PiMessage
{
    [JsonPropertyName("role")] public string? Role { get; init; }

    [JsonPropertyName("stopReason")] public string? StopReason { get; init; }

    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }

    [JsonPropertyName("usage")] public PiUsage? Usage { get; init; }
}

internal sealed record PiUsage
{
    [JsonPropertyName("input")] public long? Input { get; init; }

    [JsonPropertyName("output")] public long? Output { get; init; }

    [JsonPropertyName("cacheRead")] public long? CacheRead { get; init; }

    [JsonPropertyName("cacheWrite")] public long? CacheWrite { get; init; }

    [JsonPropertyName("cost")] public PiCost? Cost { get; init; }
}

internal sealed record PiCost
{
    [JsonPropertyName("total")] public double? Total { get; init; }
}

/// <summary>A tool-execution lifecycle event. <c>args</c> and <c>result</c> belong to the tool, not to us,
/// so they stay as JSON here and are rendered to text for display rather than traversed inward.</summary>
internal sealed record PiToolExecution
{
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; init; }

    [JsonPropertyName("toolName")] public string? ToolName { get; init; }

    [JsonPropertyName("args")] public JsonElement? Args { get; init; }

    [JsonPropertyName("result")] public PiToolResult? Result { get; init; }

    [JsonPropertyName("partialResult")] public PiToolResult? PartialResult { get; init; }

    [JsonPropertyName("isError")] public bool IsError { get; init; }
}

internal sealed record PiToolResult
{
    [JsonPropertyName("content")] public IReadOnlyList<PiContent>? Content { get; init; }
}

internal sealed record PiContent
{
    [JsonPropertyName("type")] public string? Type { get; init; }

    [JsonPropertyName("text")] public string? Text { get; init; }
}

/// <summary>An automatic retry after a transient provider error — the event Agnes exists to surface here,
/// since a retried turn is precisely what looks like a hang from the outside.</summary>
internal sealed record PiAutoRetry
{
    [JsonPropertyName("attempt")] public int Attempt { get; init; }

    [JsonPropertyName("maxAttempts")] public int MaxAttempts { get; init; }

    [JsonPropertyName("delayMs")] public int DelayMs { get; init; }

    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }

    [JsonPropertyName("success")] public bool Success { get; init; }

    [JsonPropertyName("finalError")] public string? FinalError { get; init; }
}

/// <summary>One low-level agent run finished. <c>willRetry</c> says whether Pi is about to try again, which
/// is what stops Agnes ending the turn on a failure Pi is still working through.</summary>
internal sealed record PiAgentEnd
{
    [JsonPropertyName("willRetry")] public bool WillRetry { get; init; }
}

internal sealed record PiCompaction
{
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

internal sealed record PiExtensionError
{
    [JsonPropertyName("extensionPath")] public string? ExtensionPath { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }
}
