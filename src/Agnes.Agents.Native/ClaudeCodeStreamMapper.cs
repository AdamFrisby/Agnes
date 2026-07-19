using System.Text;
using System.Text.Json;
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
                    yield return new PermissionRequestedEvent(requestId, toolUseId,
                        $"Allow {toolName}?", DefaultPermissionOptions);
                }

                break;

            case "system":
                if (GetString(line, "subtype") == "init" && GetString(line, "session_id") is { Length: > 0 } id)
                {
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
                var isError = line.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                yield return new TurnEndedEvent(isError ? StopReason.Refusal : StopReason.EndTurn);
                break;
        }
    }

    private static IEnumerable<SessionEvent> FromAssistant(JsonElement line)
    {
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
}
