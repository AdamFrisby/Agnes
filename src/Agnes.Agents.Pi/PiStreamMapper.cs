using System.Globalization;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Native;

namespace Agnes.Agents.Pi;

/// <summary>
/// Maps Pi's RPC protocol (<c>pi --mode rpc</c>) to and from Agnes's event model. Pure over each line, so
/// the whole mapping is golden-JSON testable without a CLI.
///
/// Two decisions carry most of the weight:
///
/// <para><b>A turn ends on <c>agent_settled</c>, not <c>agent_end</c>.</b> Pi distinguishes them exactly
/// because it retries: <c>agent_end</c> fires for each low-level run and carries <c>willRetry</c>, while
/// <c>agent_settled</c> means "no automatic retry, compaction retry, or queued continuation remains". Ending
/// Agnes's turn on <c>agent_end</c> would report a transient provider failure as a finished turn and throw
/// away the recovery Pi was already performing.</para>
///
/// <para><b>Retries are surfaced, not hidden.</b> Each <c>auto_retry_start</c> becomes a transcript notice.
/// A silent 8-second backoff is indistinguishable from a hang, and "the agent is retrying" is the single
/// most useful thing to know during a long unattended run.</para>
/// </summary>
public sealed class PiStreamMapper : INativeStreamMapper
{
    /// <summary>Correlation id for the opening <c>get_state</c>, so its reply is recognisable among the
    /// replies to anything else.</summary>
    internal const string HandshakeRequestId = "agnes-handshake";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Tool names Pi has produced so far, by call id — <c>tool_execution_end</c> doesn't repeat the
    /// name in every shape, and the update events need it to title the call.</summary>
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

    /// <summary>The stop reason of the most recent assistant message, carried forward because
    /// <c>agent_settled</c> — the event that actually ends the turn — states none of its own.</summary>
    private string? _lastStopReason;

    /// <inheritdoc />
    public IReadOnlyList<string> Handshake() =>
    [
        // Pi announces no session id at startup; it answers with one when asked. Agnes needs it to resume
        // this conversation later (--session-id), so ask immediately rather than at first prompt.
        Serialize(new { id = HandshakeRequestId, type = "get_state" }),
    ];

    /// <inheritdoc />
    public string? BuildCancel() => Serialize(new { type = "abort" });

    /// <inheritdoc />
    public string BuildUserTurn(IReadOnlyList<ContentBlock> content)
    {
        var text = new StringBuilder();
        var images = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent t:
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(t.Text);
                    break;
                case ImageContent i:
                    images.Add(new { type = "image", data = i.Data, mimeType = i.MimeType });
                    break;
                case ResourceLinkContent r:
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(r.Uri);
                    break;
                default:
                    break;
            }
        }

        // No streamingBehavior: Agnes serialises turns itself and holds queued messages host-side, so a
        // prompt only ever arrives when Pi is idle. Sending one anyway would change the delivery semantics
        // of a message the user expects to run now.
        return images.Count > 0
            ? Serialize(new { type = "prompt", message = text.ToString(), images })
            : Serialize(new { type = "prompt", message = text.ToString() });
    }

    /// <summary>
    /// Pi has no permission system at all — no per-tool prompt, and correspondingly no flag to skip one.
    /// So there is nothing to add here in either stance, and an attended session is refused up front by
    /// <see cref="PiAgent"/> rather than silently running unguarded.
    /// </summary>
    public IReadOnlyList<string> PermissionLaunchArguments(bool skipPermissions) => [];

    /// <inheritdoc />
    public string? BuildPermissionResponse(string requestId, bool allow) => null;

    /// <inheritdoc />
    public IEnumerable<SessionEvent> ToEvents(JsonElement line)
    {
        var kind = line.Deserialize<PiLine>(Options)?.Type;
        return kind switch
        {
            "response" => FromResponse(line),
            "message_update" => FromMessageUpdate(line),
            "message_end" => FromMessageEnd(line),
            "tool_execution_start" => FromToolStart(line),
            "tool_execution_update" => FromToolUpdate(line),
            "tool_execution_end" => FromToolEnd(line),
            "auto_retry_start" => FromRetryStart(line),
            "auto_retry_end" => FromRetryEnd(line),
            "summarization_retry_scheduled" => FromSummarizationRetry(line),
            "compaction_start" => FromCompactionStart(line),
            "compaction_end" => [new NoticeEvent("Context compacted.")],
            "extension_error" => FromExtensionError(line),
            "agent_settled" => [new TurnEndedEvent(ToStopReason(_lastStopReason), _lastStopReason)],
            _ => [],
        };
    }

    private IEnumerable<SessionEvent> FromResponse(JsonElement line)
    {
        var response = line.Deserialize<PiResponse>(Options);
        if (response is null)
        {
            yield break;
        }

        if (response.Success)
        {
            if (response.Data?.SessionId is { Length: > 0 } sessionId)
            {
                yield return new SessionStartedEvent(sessionId);
            }

            if (response.Data?.SessionName is { Length: > 0 } name)
            {
                yield return new SessionTitleEvent(name);
            }

            yield break;
        }

        // A rejected command is a real fault (a bad model id, a prompt sent while streaming) and the user
        // gets no other signal it happened.
        yield return new AgentErrorEvent(
            $"Pi rejected '{response.Command ?? "command"}': {response.Error ?? "unknown error"}");
    }

    private IEnumerable<SessionEvent> FromMessageUpdate(JsonElement line)
    {
        var delta = line.Deserialize<PiMessageUpdate>(Options)?.AssistantMessageEvent;
        if (delta is null)
        {
            yield break;
        }

        switch (delta.Type)
        {
            case "text_delta" when delta.Delta is { Length: > 0 } text:
                yield return new MessageChunkEvent(MessageRole.Assistant, new TextContent(text));
                break;
            case "thinking_delta" when delta.Delta is { Length: > 0 } thinking:
                yield return new ThoughtChunkEvent(new TextContent(thinking));
                break;
            case "toolcall_start" when delta.Id is { Length: > 0 } id && delta.ToolName is { Length: > 0 } tool:
                // Remember the name now: the execution events that follow carry the id, and the tool-call
                // card is titled from the name.
                _toolNames[id] = tool;
                break;
            default:
                break;
        }
    }

    private IEnumerable<SessionEvent> FromMessageEnd(JsonElement line)
    {
        var message = line.Deserialize<PiMessageEnvelope>(Options)?.Message;
        if (message is null)
        {
            yield break;
        }

        if (!string.Equals(message.Role, "assistant", StringComparison.Ordinal))
        {
            yield break;
        }

        _lastStopReason = message.StopReason;

        if (message.Usage is { } usage)
        {
            var input = (usage.Input ?? 0) + (usage.CacheRead ?? 0) + (usage.CacheWrite ?? 0);
            yield return new UsageReportedEvent(new UsageMetrics(
                ContextUsed: input > 0 ? input : null,
                OutputTokens: usage.Output,
                CostUsd: usage.Cost?.Total));
        }

        // An errored message that Pi is about to retry is reported by the retry events instead; only a
        // message that stopped in error and settles that way is a fault the user must see. agent_end
        // carries willRetry, so the error is deferred to the retry/settle path rather than raised here.
    }

    private IEnumerable<SessionEvent> FromToolStart(JsonElement line)
    {
        var tool = line.Deserialize<PiToolExecution>(Options);
        if (tool?.ToolCallId is not { Length: > 0 } id)
        {
            yield break;
        }

        var name = tool.ToolName ?? _toolNames.GetValueOrDefault(id) ?? "tool";
        _toolNames[id] = name;
        yield return new ToolCallEvent(id, name, ToKind(name), ToolCallStatus.InProgress, Describe(tool.Args));
    }

    private static IEnumerable<SessionEvent> FromToolUpdate(JsonElement line)
    {
        var tool = line.Deserialize<PiToolExecution>(Options);
        if (tool?.ToolCallId is not { Length: > 0 } id)
        {
            yield break;
        }

        yield return new ToolCallUpdateEvent(id, ToolCallStatus.InProgress, ToContent(tool.PartialResult));
    }

    private static IEnumerable<SessionEvent> FromToolEnd(JsonElement line)
    {
        var tool = line.Deserialize<PiToolExecution>(Options);
        if (tool?.ToolCallId is not { Length: > 0 } id)
        {
            yield break;
        }

        yield return new ToolCallUpdateEvent(
            id,
            tool.IsError ? ToolCallStatus.Failed : ToolCallStatus.Completed,
            ToContent(tool.Result));
    }

    private static IEnumerable<SessionEvent> FromRetryStart(JsonElement line)
    {
        var retry = line.Deserialize<PiAutoRetry>(Options);
        if (retry is null)
        {
            yield break;
        }

        var seconds = (retry.DelayMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
        var reason = string.IsNullOrWhiteSpace(retry.ErrorMessage) ? "provider error" : retry.ErrorMessage.Trim();
        yield return new NoticeEvent(
            $"Provider error; retrying in {seconds}s (attempt {retry.Attempt} of {retry.MaxAttempts}): {reason}");
    }

    private static IEnumerable<SessionEvent> FromRetryEnd(JsonElement line)
    {
        var retry = line.Deserialize<PiAutoRetry>(Options);
        if (retry is null)
        {
            yield break;
        }

        if (retry.Success)
        {
            yield return new NoticeEvent($"Recovered after {retry.Attempt} retr{(retry.Attempt == 1 ? "y" : "ies")}.");
            yield break;
        }

        // Retries exhausted: this is where the turn genuinely failed, and the only place Agnes should say so.
        yield return new AgentErrorEvent(
            $"Gave up after {retry.Attempt} retries: {retry.FinalError ?? retry.ErrorMessage ?? "provider error"}");
    }

    private static IEnumerable<SessionEvent> FromSummarizationRetry(JsonElement line)
    {
        var retry = line.Deserialize<PiAutoRetry>(Options);
        if (retry is null)
        {
            yield break;
        }

        yield return new NoticeEvent(
            $"Compaction hit a provider error; retrying (attempt {retry.Attempt} of {retry.MaxAttempts}).");
    }

    private static IEnumerable<SessionEvent> FromCompactionStart(JsonElement line)
    {
        var reason = line.Deserialize<PiCompaction>(Options)?.Reason;
        yield return new NoticeEvent(
            reason is { Length: > 0 } r ? $"Compacting context ({r})…" : "Compacting context…");
    }

    private static IEnumerable<SessionEvent> FromExtensionError(JsonElement line)
    {
        var error = line.Deserialize<PiExtensionError>(Options);
        if (error is null)
        {
            yield break;
        }

        yield return new NoticeEvent(
            $"Pi extension error ({error.ExtensionPath ?? "unknown"}): {error.Error ?? "unknown error"}",
            IsError: true);
    }

    /// <summary>Renders a tool's own JSON input for display. The schema belongs to the tool, so it is shown
    /// verbatim rather than traversed — a shell command must reach the transcript uninterpreted.</summary>
    private static IReadOnlyList<ContentBlock> Describe(JsonElement? args)
        => args is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } value
            ? [new TextContent(value.ToString())]
            : [];

    private static IReadOnlyList<ContentBlock>? ToContent(PiToolResult? result)
    {
        if (result?.Content is not { Count: > 0 } blocks)
        {
            return null;
        }

        var text = string.Join('\n', blocks
            .Where(b => string.Equals(b.Type, "text", StringComparison.Ordinal) && !string.IsNullOrEmpty(b.Text))
            .Select(b => b.Text));

        return string.IsNullOrEmpty(text) ? null : [new TextContent(text)];
    }

    /// <summary>Pi's built-in tool vocabulary, mapped onto Agnes's cross-adapter taxonomy. Extension tools
    /// fall through to <see cref="ToolKind.Other"/>, which is the intended default.</summary>
    internal static ToolKind ToKind(string name) => name switch
    {
        "read" => ToolKind.Read,
        "edit" or "write" => ToolKind.Edit,
        "bash" or "powershell" => ToolKind.Execute,
        "grep" or "find" or "ls" => ToolKind.Search,
        _ => ToolKind.Other,
    };

    /// <summary>Pi's stop reasons, narrowed to Agnes's set. An unrecognised one is kept verbatim on the
    /// event's raw reason, so nothing is lost by this narrowing.</summary>
    internal static StopReason ToStopReason(string? stopReason) => stopReason switch
    {
        "length" => StopReason.MaxTokens,
        "aborted" => StopReason.Cancelled,
        _ => StopReason.EndTurn,
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
