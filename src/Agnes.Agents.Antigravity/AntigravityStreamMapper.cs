using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Native;

namespace Agnes.Agents.Antigravity;

/// <summary>
/// Maps Antigravity's print-mode stream to Agnes's <see cref="SessionEvent"/> model.
///
/// <para>The protocol is described in <see cref="AntigravityEvents"/>. Two things about it shape this
/// mapper. Tool steps arrive twice — <c>ACTIVE</c> then <c>DONE</c> or <c>ERROR</c> — under a stable
/// <c>step_index</c>, so the index is the tool-call id and the second frame is an update rather than a
/// new call. And a turn ends with a <c>result</c> frame carrying a rising <c>num_turns</c>, not with the
/// process exiting: the same process serves every turn of the conversation.</para>
/// </summary>
internal sealed class AntigravityStreamMapper : INativeStreamMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IEnumerable<SessionEvent> ToEvents(JsonElement line)
    {
        if (!line.TryGetProperty("event", out var eventName) || eventName.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        switch (eventName.GetString())
        {
            case AntigravityEvents.Init:
                // The conversation id is announced once, here, and is what --conversation resumes with.
                if (line.TryGetProperty("conversation_id", out var id) && id.GetString() is { Length: > 0 } sessionId)
                {
                    yield return new SessionStartedEvent(sessionId);
                }

                break;

            case AntigravityEvents.StepUpdate:
                if (line.TryGetProperty("step_update", out var stepJson)
                    && Deserialize<AntigravityStep>(stepJson) is { } step)
                {
                    foreach (var e in FromStep(step))
                    {
                        yield return e;
                    }
                }

                break;

            case AntigravityEvents.Result:
                if (line.TryGetProperty("result", out var resultJson)
                    && Deserialize<AntigravityResult>(resultJson) is { } result)
                {
                    foreach (var e in FromResult(result))
                    {
                        yield return e;
                    }
                }

                break;

            default:
                // An unrecognised frame is not an error: the CLI is proprietary and versioned
                // independently, so a new frame kind is expected to appear before Agnes knows it.
                break;
        }
    }

    private static IEnumerable<SessionEvent> FromStep(AntigravityStep step)
    {
        switch (step.StepType)
        {
            case AntigravityStepTypes.AgentResponse:
                if (step.TextDelta is { Length: > 0 } text)
                {
                    yield return new MessageChunkEvent(MessageRole.Assistant, new TextContent(text));
                }

                if (step.Usage is { } usage)
                {
                    yield return UsageOf(usage);
                }

                break;

            case AntigravityStepTypes.Tool:
                // step_index is stable across the ACTIVE and DONE frames of one call, so it is the id.
                var toolId = step.StepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var name = step.ToolName ?? step.ToolInfo?.Name ?? "tool";

                if (IsActive(step.State))
                {
                    yield return new ToolCallEvent(
                        toolId,
                        name,
                        ToolKindOf(name),
                        ToolCallStatus.InProgress,
                        Arguments(step.ToolInfo));
                }
                else
                {
                    yield return new ToolCallUpdateEvent(
                        toolId,
                        IsError(step.State) ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                        []);
                }

                break;

            case AntigravityStepTypes.UserInput:
            case AntigravityStepTypes.SystemMessage:
            default:
                // user_input only echoes that the prompt was accepted — Agnes already recorded the
                // prompt it sent — and system_message carries no payload at all. Emitting either would
                // duplicate the transcript or add a blank line to it.
                break;
        }
    }

    private static IEnumerable<SessionEvent> FromResult(AntigravityResult result)
    {
        if (result.Usage is { } usage)
        {
            yield return UsageOf(usage);
        }

        var failed = string.Equals(result.Status, "ERROR", StringComparison.OrdinalIgnoreCase);
        if (failed)
        {
            yield return new AgentErrorEvent(
                result.Error is { Length: > 0 } error ? error : "Antigravity reported an error with no message.");
        }

        // One result per turn; the process stays open for the next. So this ends the TURN, never the
        // session — treating it as the end of the conversation is what would break multi-turn.
        yield return new TurnEndedEvent(failed ? StopReason.Refusal : StopReason.EndTurn);
    }

    private static UsageReportedEvent UsageOf(AntigravityUsage usage)
        => new(new UsageMetrics(
            InputTokens: usage.InputTokens,
            OutputTokens: usage.OutputTokens,
            CacheReadTokens: usage.CacheReadTokens,
            // Antigravity bills through a Google subscription and reports no per-turn cost, so cost is
            // left unset rather than invented from a token count and a guessed rate.
            CostUsd: null));

    /// <summary>
    /// Tool arguments, verbatim. They are the tool's own schema, not Agnes's, so they are passed through
    /// as text rather than reshaped into fields this side does not own.
    /// </summary>
    private static IReadOnlyList<ContentBlock> Arguments(AntigravityToolInfo? info)
        => info?.Parameters is { } parameters && parameters.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            ? [new TextContent(parameters.GetRawText())]
            : [];

    /// <summary>
    /// Maps Antigravity's tool names onto Agnes's kinds. The names come from the <c>init</c> frame's tool
    /// list on a live CLI; anything unrecognised stays <see cref="ToolKind.Other"/> rather than being
    /// guessed at from a substring, which would mis-file a tool the moment Google renames one.
    /// </summary>
    private static ToolKind ToolKindOf(string name) => name switch
    {
        "run_command" => ToolKind.Execute,
        "view_file" or "view_code_item" or "view_line_range" => ToolKind.Read,
        "replace_file_content" or "write_to_file" or "edit_file" => ToolKind.Edit,
        "find_by_name" or "grep_search" or "codebase_search" or "list_dir" => ToolKind.Search,
        "read_url_content" or "search_web" => ToolKind.Fetch,
        _ => ToolKind.Other,
    };

    private static bool IsActive(string? state)
        => string.Equals(state, AntigravityStepStates.Active, StringComparison.Ordinal);

    private static bool IsError(string? state)
        => string.Equals(state, AntigravityStepStates.Error, StringComparison.Ordinal);

    private static T? Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(Json);
        }
        catch (JsonException)
        {
            // A frame we cannot read must not take the stream down with it.
            return default;
        }
    }

    /// <summary>
    /// One NDJSON line per user turn. The envelope is <c>event</c>-keyed — Antigravity's own naming —
    /// while the inner message matches Claude's content-block shape.
    /// </summary>
    public string BuildUserTurn(IReadOnlyList<ContentBlock> content)
    {
        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        return JsonSerializer.Serialize(new
        {
            @event = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "text", text } },
            },
        }, Json);
    }

    /// <summary>
    /// <c>--dangerously-skip-permissions</c>, always — and the adapter refuses any session that did not
    /// ask for it.
    ///
    /// <para>Omitting the flag does not make Antigravity ask. Verified against agy 1.1.24: without it the
    /// CLI silently redirects file writes to <c>~/.gemini/antigravity-cli/scratch/</c> and reports
    /// <c>SUCCESS</c>, leaving the working directory untouched. A caller who read "no skip flag" as "safe"
    /// would get a convincing transcript describing edits that never happened.</para>
    /// </summary>
    public IReadOnlyList<string> PermissionLaunchArguments(bool skipPermissions)
        => ["--dangerously-skip-permissions"];

    /// <summary>Antigravity has no permission protocol to answer — see
    /// <see cref="PermissionLaunchArguments"/>.</summary>
    public string? BuildPermissionResponse(string requestId, bool allow) => null;
}
