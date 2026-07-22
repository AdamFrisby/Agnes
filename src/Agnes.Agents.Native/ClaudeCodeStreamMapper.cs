using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agnes.Abstractions;

namespace Agnes.Agents.Native;

/// <summary>
/// Maps Claude Code's native stream-json (the SDK/headless <c>--output-format stream-json</c>) to
/// Agnes events. Line types: <c>system/init</c>, <c>assistant</c>, <c>user</c> (tool results),
/// <c>result</c>. The Task tool becomes a <see cref="SubagentStartedEvent"/> so subagents show in
/// the tree. (Exact flags + the permission/cancel control protocol need live tuning against the CLI.)
/// </summary>
public sealed class ClaudeCodeStreamMapper : INativeStreamMapper
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // The model's real context window, learned from the system/init line. Null until known (or if
    // the model is unrecognized) — we never guess a window, we just omit the "/ max" when unsure.
    private long? _contextWindow;

    public IEnumerable<SessionEvent> ToEvents(JsonElement line)
    {
        var type = GetString(line, "type");
        switch (type)
        {
            case "control_request":
                // claude asks permission for a tool over the stdio control channel (enabled by
                // --permission-prompt-tool stdio). Surface it as a permission request the user answers.
                if (line.TryGetProperty("request", out var req)
                    && GetString(req, "subtype") == "can_use_tool"
                    && GetString(line, "request_id") is { Length: > 0 } requestId)
                {
                    var toolName = GetString(req, "tool_name") ?? "tool";
                    var toolUseId = GetString(req, "tool_use_id") ?? requestId;
                    // AskUserQuestion rides the SAME can_use_tool channel, but its "answer" is structured
                    // (options + notes) rather than allow/deny — surface it as a QuestionAskedEvent and stash
                    // the original input so the answer can echo it back in updatedInput.
                    if (toolName == "AskUserQuestion" && req.TryGetProperty("input", out var questionInput))
                    {
                        _questionInputs[requestId] = questionInput.GetRawText();
                        yield return new QuestionAskedEvent(requestId, toolUseId, ParseQuestions(questionInput));
                    }
                    else
                    {
                        yield return new PermissionRequestedEvent(requestId, toolUseId,
                            $"Allow {toolName}?", DefaultPermissionOptions);
                    }
                }

                break;

            case "system":
                if (GetString(line, "subtype") == "init" && GetString(line, "session_id") is { Length: > 0 } id)
                {
                    _contextWindow = ContextWindowFor(GetString(line, "model"));
                    yield return new SessionStartedEvent(id);
                }

                break;

            case "assistant":
                foreach (var e in FromAssistant(line))
                {
                    yield return e;
                }

                break;

            case "user":
                foreach (var e in FromUser(line))
                {
                    yield return e;
                }

                break;

            case "result":
                // The result line carries the run's real cost — surface it (context tokens come from
                // the per-message usage below, which reflects live window occupancy).
                if (GetDouble(line, "total_cost_usd") is { } cost)
                {
                    yield return new UsageReportedEvent(CostUsd: cost);
                }

                var isError = line.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                yield return new TurnEndedEvent(isError ? StopReason.Refusal : StopReason.EndTurn);
                break;
        }
    }

    private IEnumerable<SessionEvent> FromAssistant(JsonElement line)
    {
        // Each assistant message carries a usage block; input + cache tokens are the current
        // context-window occupancy the model reported. Emit it as real usage (never estimated).
        if (line.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var usage))
        {
            var context = ContextTokensFrom(usage);
            var output = GetLong(usage, "output_tokens");
            if (context is not null || output is not null)
            {
                yield return new UsageReportedEvent(ContextTokens: context, ContextWindow: _contextWindow, OutputTokens: output);
            }
        }

        if (!TryGetContentArray(line, out var content))
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } text:
                    yield return new MessageChunkEvent(MessageRole.Assistant, new TextContent(text));
                    break;

                case "tool_use":
                    var name = GetString(block, "name") ?? "tool";
                    var toolId = GetString(block, "id") ?? Guid.NewGuid().ToString("n");
                    if (name == "Task")
                    {
                        var sub = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                            ? GetString(input, "description") ?? GetString(input, "subagent_type") ?? "subagent"
                            : "subagent";
                        yield return new SubagentStartedEvent(toolId, sub);
                    }
                    else
                    {
                        yield return new ToolCallEvent(toolId, ToolTitle(name, block), ToKind(name), ToolCallStatus.InProgress,
                            [new TextContent(InputSummary(block))]);
                    }

                    break;
            }
        }
    }

    private static IEnumerable<SessionEvent> FromUser(JsonElement line)
    {
        if (!TryGetContentArray(line, out var content))
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") == "tool_result" && GetString(block, "tool_use_id") is { Length: > 0 } toolId)
            {
                var text = block.TryGetProperty("content", out var c) ? ContentText(c) : string.Empty;
                yield return new ToolCallUpdateEvent(toolId, ToolCallStatus.Completed, [new TextContent(text)]);
            }
        }
    }

    public string BuildUserTurn(IReadOnlyList<ContentBlock> content)
    {
        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        var payload = new
        {
            type = "user",
            message = new { role = "user", content = new[] { new { type = "text", text } } },
        };
        return JsonSerializer.Serialize(payload, Options);
    }

    // "stdio" routes each tool-permission decision to a can_use_tool control_request on the stream
    // (which we answer). --dangerously-skip-permissions is the opt-in autonomous mode.
    public IReadOnlyList<string> PermissionLaunchArguments(bool skipPermissions)
        => skipPermissions ? ["--dangerously-skip-permissions"] : ["--permission-prompt-tool", "stdio"];

    public string BuildPermissionResponse(string requestId, bool allow)
    {
        var response = allow
            ? (object)new { subtype = "success", request_id = requestId, response = new { behavior = "allow" } }
            : new { subtype = "success", request_id = requestId, response = new { behavior = "deny", message = "Denied by the user." } };
        return JsonSerializer.Serialize(new { type = "control_response", response }, Options);
    }

    // Pending AskUserQuestion inputs by request id, so the answer can echo the original questions back in
    // updatedInput (the CLI re-validates updatedInput against the tool schema). Cross-thread: read loop
    // adds, the answer call removes.
    private readonly ConcurrentDictionary<string, string> _questionInputs = new();

    private static IReadOnlyList<AgentQuestion> ParseQuestions(JsonElement input)
    {
        var questions = new List<AgentQuestion>();
        if (input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
            {
                var prompt = GetString(q, "question") ?? string.Empty;
                var options = new List<QuestionChoice>();
                if (q.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in os.EnumerateArray())
                    {
                        options.Add(new QuestionChoice(GetString(o, "label") ?? string.Empty, GetString(o, "description") ?? string.Empty));
                    }
                }

                var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
                // Claude keys its answers map by the question TEXT, so use the prompt as the id to round-trip.
                questions.Add(new AgentQuestion(prompt, GetString(q, "header") ?? string.Empty, prompt, options, multi));
            }
        }

        return questions;
    }

    /// <summary>
    /// Builds the control_response for an AskUserQuestion. With answers it allows and echoes the original
    /// input plus an <c>answers</c> map (question text → chosen label, or comma-joined for multi-select) and
    /// <c>questionStates</c> (carries free-text notes in <c>textInputValue</c>). Empty answers = the user
    /// dismissed it → deny with feedback. Returns null if the request id isn't a pending question.
    /// </summary>
    public string? BuildQuestionResponse(string requestId, IReadOnlyList<QuestionAnswer> answers)
    {
        if (!_questionInputs.TryRemove(requestId, out var inputJson))
        {
            return null;
        }

        JsonObject inner;
        if (answers.Count == 0)
        {
            inner = new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = "The user dismissed the questions.",
                ["feedback"] = "The user dismissed the questions without answering.",
            };
        }
        else
        {
            var updated = JsonNode.Parse(inputJson)?.AsObject() ?? new JsonObject();
            var answersObj = new JsonObject();
            var statesObj = new JsonObject();
            foreach (var a in answers)
            {
                var joined = string.Join(", ", a.SelectedLabels);
                answersObj[a.QuestionId] = joined;
                statesObj[a.QuestionId] = new JsonObject
                {
                    ["selectedValue"] = a.SelectedLabels.Count == 1 ? a.SelectedLabels[0] : joined,
                    ["textInputValue"] = a.Notes ?? string.Empty,
                };
            }

            updated["answers"] = answersObj;
            updated["questionStates"] = statesObj;
            inner = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated };
        }

        var response = new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = inner,
            },
        };
        return response.ToJsonString(Options);
    }

    private static readonly PermissionOption[] DefaultPermissionOptions =
    [
        new("allow", "Allow", PermissionOptionKind.AllowOnce),
        new("reject", "Reject", PermissionOptionKind.RejectOnce),
    ];

    private static ToolKind ToKind(string name) => name switch
    {
        "Read" => ToolKind.Read,
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => ToolKind.Edit,
        "Bash" or "BashOutput" => ToolKind.Execute,
        "Grep" or "Glob" => ToolKind.Search,
        "WebFetch" or "WebSearch" => ToolKind.Fetch,
        _ => ToolKind.Other,
    };

    private static string ToolTitle(string name, JsonElement block)
    {
        if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            var target = GetString(input, "file_path") ?? GetString(input, "path") ?? GetString(input, "pattern") ?? GetString(input, "command");
            if (!string.IsNullOrEmpty(target))
            {
                return target!.Length > 80 ? target[..80] + "…" : target;
            }
        }

        return name;
    }

    private static string InputSummary(JsonElement block)
        => block.TryGetProperty("input", out var input) ? input.GetRawText() : string.Empty;

    private static bool TryGetContentArray(JsonElement line, out JsonElement content)
    {
        content = default;
        return line.TryGetProperty("message", out var msg)
               && msg.TryGetProperty("content", out content)
               && content.ValueKind == JsonValueKind.Array;
    }

    private static string ContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var b in content.EnumerateArray())
            {
                if (GetString(b, "type") == "text")
                {
                    sb.Append(GetString(b, "text"));
                }
            }

            return sb.ToString();
        }

        return string.Empty;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
           && p.TryGetInt64(out var v)
            ? v
            : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
           && p.TryGetDouble(out var v)
            ? v
            : null;

    // Context-window occupancy = the model's prompt-side tokens for this message: fresh input plus
    // the cache-read and cache-creation tokens (all count against the window). Null if none present.
    private static long? ContextTokensFrom(JsonElement usage)
    {
        long sum = 0;
        var any = false;
        foreach (var key in ContextTokenKeys)
        {
            if (GetLong(usage, key) is { } v)
            {
                sum += v;
                any = true;
            }
        }

        return any ? sum : null;
    }

    private static readonly string[] ContextTokenKeys =
        ["input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"];

    // The model's real context window. 200k is the standard window across the modern Claude lineup;
    // the "1m" long-context variants expose 1M. Unrecognized models return null (no window shown).
    private static long? ContextWindowFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var m = model.ToLowerInvariant();
        if (m.Contains("1m"))
        {
            return 1_000_000;
        }

        return m.Contains("claude") ? 200_000 : null;
    }
}
