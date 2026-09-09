using System.Text.Json.Serialization;

namespace Agnes.Agents.Antigravity;

/// <summary>
/// Antigravity's print-mode stream protocol, as observed against <c>agy 1.1.24</c>.
///
/// <para>It is <b>not</b> Claude Code's stream-json, despite the identical flag names. Every frame is
/// keyed on <c>event</c> rather than <c>type</c>, and the CLI says so when you get it wrong — feeding it
/// a Claude-shaped line answers <c>"stream input message is missing the \"event\" field"</c>. Only the
/// inner <c>message</c> object matches Claude's shape.</para>
///
/// <para>Three frames exist: <c>init</c> once at startup, <c>step_update</c> many times, and
/// <c>result</c> once per turn. There is no separate stream-end frame — a persistent process emits one
/// <c>result</c> per NDJSON line it is fed, and stays open for the next.</para>
/// </summary>
internal static class AntigravityEvents
{
    public const string Init = "init";
    public const string StepUpdate = "step_update";
    public const string Result = "result";
}

/// <summary>Step lifecycle. <c>ACTIVE</c> then <c>DONE</c>, or <c>ERROR</c> in place of <c>DONE</c>.</summary>
internal static class AntigravityStepStates
{
    public const string Active = "ACTIVE";
    public const string Done = "DONE";
    public const string Error = "ERROR";
}

/// <summary>
/// The kinds of step the CLI reports. <c>user_input</c> acknowledges the prompt, <c>agent_response</c>
/// carries assistant text in <c>text_delta</c>, <c>tool</c> carries a tool call, and
/// <c>system_message</c> is an internal marker that carries nothing renderable.
/// </summary>
internal static class AntigravityStepTypes
{
    public const string UserInput = "user_input";
    public const string AgentResponse = "agent_response";
    public const string Tool = "tool";
    public const string SystemMessage = "system_message";
}

internal sealed record AntigravityInit
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    /// <summary>Every tool the CLI has available. Read for diagnostics, not modelled per-tool.</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<string>? Tools { get; init; }
}

internal sealed record AntigravityStep
{
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("step_index")]
    public int StepIndex { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("step_type")]
    public string? StepType { get; init; }

    /// <summary>Assistant text. Named a delta but observed to arrive whole, once, on the DONE step —
    /// so it is appended rather than accumulated, and an absent value means "this step said nothing".</summary>
    [JsonPropertyName("text_delta")]
    public string? TextDelta { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_info")]
    public AntigravityToolInfo? ToolInfo { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("usage")]
    public AntigravityUsage? Usage { get; init; }
}

internal sealed record AntigravityToolInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Tool arguments. Left as raw JSON deliberately: this is a genuine external boundary whose shape is
    /// the tool's own — <c>find_by_name</c> takes <c>Pattern</c>/<c>SearchDirectory</c>,
    /// <c>run_command</c> takes something else entirely — and there is no schema Agnes owns to map it to.
    /// </summary>
    [JsonPropertyName("parameters")]
    public System.Text.Json.JsonElement? Parameters { get; init; }
}

internal sealed record AntigravityUsage
{
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("thinking_tokens")]
    public long ThinkingTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }
}

internal sealed record AntigravityResult
{
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    /// <summary><c>SUCCESS</c> or <c>ERROR</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("response")]
    public string? Response { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Turns completed in this process so far — 1, then 2, … — which is how a persistent
    /// session distinguishes "this turn ended" from "the process ended".</summary>
    [JsonPropertyName("num_turns")]
    public int NumTurns { get; init; }

    [JsonPropertyName("usage")]
    public AntigravityUsage? Usage { get; init; }
}
