using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Codex.Wire;

namespace Agnes.Agents.Codex;

/// <summary>
/// Maps the Codex app-server protocol (thread/turn/item notifications) to Agnes
/// <see cref="SessionEvent"/>s, and Agnes prompts/decisions the other way. Pure and golden-JSON
/// testable, like <c>ClaudeCodeStreamMapper</c> and the ACP mapper. One instance per session — it
/// remembers which message items streamed deltas (so a final <c>item/completed</c> doesn't
/// duplicate text) and which tool items already started (so a completion updates rather than
/// re-adds), plus the model's context window learned from token-usage notifications.
/// </summary>
internal sealed class CodexMap
{
    private readonly HashSet<string> _streamedMessages = [];
    private readonly HashSet<string> _startedTools = [];
    private long? _contextWindow;

    // ---- inbound: item lifecycle -> events ----

    /// <summary>An <c>item/started</c> notification: a tool call begins (messages stream via deltas).</summary>
    public IEnumerable<SessionEvent> ItemStarted(JsonElement notification)
    {
        if (!TryGetItem(notification, out var item, out var type))
        {
            yield break;
        }

        foreach (var e in ToolStart(item, type))
        {
            yield return e;
        }
    }

    /// <summary>An <c>item/completed</c> notification: final message text, or a tool call's result.</summary>
    public IEnumerable<SessionEvent> ItemCompleted(JsonElement notification)
    {
        if (!TryGetItem(notification, out var item, out var type))
        {
            yield break;
        }

        var id = Str(item, "id") ?? string.Empty;
        switch (type)
        {
            case "agentMessage":
                // Skip if it already streamed via item/agentMessage/delta (avoid duplicating the text).
                if (!_streamedMessages.Contains(id) && Str(item, "text") is { Length: > 0 } text)
                {
                    yield return new MessageChunkEvent(MessageRole.Assistant, new TextContent(text));
                }

                break;

            case "reasoning":
                if (ReasoningText(item) is { Length: > 0 } thought)
                {
                    yield return new ThoughtChunkEvent(new TextContent(thought));
                }

                break;

            case "plan":
                if (PlanEntries(item) is { Count: > 0 } entries)
                {
                    yield return new PlanEvent(entries);
                }

                break;

            case "userMessage":
                // The host records the user's prompt itself (HostSession); don't echo it back.
                break;

            default:
                // Any tool-like item (commandExecution/fileChange/webSearch/mcpToolCall/...): finalize it.
                foreach (var e in ToolComplete(item, type))
                {
                    yield return e;
                }

                break;
        }
    }

    /// <summary>An <c>item/agentMessage/delta</c> notification: a streamed chunk of assistant text.</summary>
    public SessionEvent? AgentMessageDelta(JsonElement notification)
    {
        if (Str(notification, "itemId") is { Length: > 0 } id && Str(notification, "delta") is { Length: > 0 } delta)
        {
            _streamedMessages.Add(id);
            return new MessageChunkEvent(MessageRole.Assistant, new TextContent(delta));
        }

        return null;
    }

    /// <summary>A <c>thread/tokenUsage/updated</c> notification: real token usage (never estimated).</summary>
    public SessionEvent? TokenUsage(JsonElement notification)
    {
        if (!notification.TryGetProperty("tokenUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Long(usage, "modelContextWindow") is { } window)
        {
            _contextWindow = window;
        }

        // Context occupancy = the prompt-side tokens of the latest turn (fresh input + cached input).
        var total = usage.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Object ? t : usage;
        var input = Long(total, "inputTokens");
        var cached = Long(total, "cachedInputTokens");
        long? context = input is null && cached is null ? null : (input ?? 0) + (cached ?? 0);
        var output = Long(total, "outputTokens");

        if (context is null && output is null)
        {
            return null;
        }

        return new UsageReportedEvent(ContextTokens: context, ContextWindow: _contextWindow, OutputTokens: output);
    }

    // ---- tool items ----

    private IEnumerable<SessionEvent> ToolStart(JsonElement item, string type)
    {
        var id = Str(item, "id");
        if (id is null)
        {
            yield break;
        }

        if (type == "subAgentActivity")
        {
            yield return new SubagentStartedEvent(Str(item, "agentThreadId") ?? id, Str(item, "kind") ?? "subagent");
            yield break;
        }

        _startedTools.Add(id);
        yield return new ToolCallEvent(id, ToolTitle(item, type), ToolKindFor(type), ToolCallStatus.InProgress,
            [new TextContent(ToolDetail(item, type))]);
    }

    private IEnumerable<SessionEvent> ToolComplete(JsonElement item, string type)
    {
        var id = Str(item, "id");
        if (id is null || type == "subAgentActivity")
        {
            yield break;
        }

        var status = ToStatus(Str(item, "status"), completed: true);
        var detail = ToolDetail(item, type);

        // If we saw the start, update it in place; otherwise surface it as a completed call.
        if (_startedTools.Remove(id))
        {
            yield return new ToolCallUpdateEvent(id, status, [new TextContent(detail)]);
        }
        else
        {
            yield return new ToolCallEvent(id, ToolTitle(item, type), ToolKindFor(type), status, [new TextContent(detail)]);
        }
    }

    private static ToolKind ToolKindFor(string type) => type switch
    {
        "commandExecution" => ToolKind.Execute,
        "fileChange" => ToolKind.Edit,
        "webSearch" => ToolKind.Fetch,
        "imageView" => ToolKind.Read,
        _ => ToolKind.Other,
    };

    private static string ToolTitle(JsonElement item, string type) => type switch
    {
        "commandExecution" => CommandText(item) is { Length: > 0 } c ? c : "command",
        "fileChange" => FileChangeTitle(item),
        "webSearch" => Str(item, "query") ?? "web search",
        "mcpToolCall" or "dynamicToolCall" => Str(item, "tool") ?? "tool",
        "imageView" => Str(item, "path") ?? "image",
        _ => type,
    };

    private static string ToolDetail(JsonElement item, string type) => type switch
    {
        "commandExecution" => Str(item, "aggregatedOutput") ?? CommandText(item) ?? string.Empty,
        "fileChange" => FileChangeSummary(item),
        "webSearch" => Str(item, "query") ?? string.Empty,
        "mcpToolCall" or "dynamicToolCall" => item.TryGetProperty("arguments", out var a) ? a.GetRawText() : string.Empty,
        _ => string.Empty,
    };

    private static string? CommandText(JsonElement item)
    {
        if (!item.TryGetProperty("command", out var c))
        {
            return null;
        }

        if (c.ValueKind == JsonValueKind.String)
        {
            return c.GetString();
        }

        if (c.ValueKind == JsonValueKind.Array)
        {
            return string.Join(' ', c.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null));
        }

        return null;
    }

    private static string FileChangeTitle(JsonElement item)
    {
        var paths = ChangePaths(item).ToList();
        return paths.Count switch
        {
            0 => "file change",
            1 => paths[0],
            _ => $"{paths.Count} files",
        };
    }

    private static string FileChangeSummary(JsonElement item)
    {
        var sb = new StringBuilder();
        foreach (var path in ChangePaths(item))
        {
            sb.Append(path).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static IEnumerable<string> ChangePaths(JsonElement item)
    {
        if (item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in changes.EnumerateArray())
            {
                if (Str(change, "path") is { Length: > 0 } path)
                {
                    yield return path;
                }
            }
        }
    }

    private static string ReasoningText(JsonElement item)
    {
        if (Str(item, "summary") is { Length: > 0 } summary)
        {
            return summary;
        }

        if (item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    sb.Append(Str(block, "text"));
                }

                return sb.ToString();
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<PlanEntry> PlanEntries(JsonElement item)
    {
        var text = Str(item, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var entries = new List<PlanEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // Recognize simple markdown checkboxes ("- [x] done", "- [ ] todo").
            var status = "pending";
            if (line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("- [X]"))
            {
                status = "completed";
                line = line[5..].Trim();
            }
            else if (line.StartsWith("- [ ]"))
            {
                line = line[5..].Trim();
            }
            else if (line.StartsWith("- "))
            {
                line = line[2..].Trim();
            }

            if (line.Length > 0)
            {
                entries.Add(new PlanEntry(line, status));
            }
        }

        return entries;
    }

    // ---- outbound: prompt + decision ----

    /// <summary>Builds the <c>turn/start</c> input from Agnes content (text today; images later).</summary>
    public static IReadOnlyList<CodexUserInput> ToInput(IReadOnlyList<ContentBlock> content)
    {
        var items = new List<CodexUserInput>();
        foreach (var block in content)
        {
            var text = block switch
            {
                TextContent t => t.Text,
                ResourceLinkContent r => $"@{r.Uri}",
                _ => null,
            };

            if (!string.IsNullOrEmpty(text))
            {
                items.Add(new CodexUserInput("text", text));
            }
        }

        return items;
    }

    /// <summary>Maps an allow/deny choice to Codex's ReviewDecision string.</summary>
    public static string Decision(bool allow) => allow ? "approved" : "denied";

    /// <summary>Maps a Codex turn status to a stop reason.</summary>
    public static StopReason ToStopReason(string? status) => status switch
    {
        "completed" or "success" => StopReason.EndTurn,
        "interrupted" or "cancelled" or "canceled" => StopReason.Cancelled,
        "failed" or "error" => StopReason.Refusal,
        _ => StopReason.EndTurn,
    };

    // ---- json helpers ----

    private static bool TryGetItem(JsonElement notification, out JsonElement item, out string type)
    {
        item = default;
        type = string.Empty;
        if (notification.TryGetProperty("item", out item) && item.ValueKind == JsonValueKind.Object
            && Str(item, "type") is { Length: > 0 } t)
        {
            type = t;
            return true;
        }

        return false;
    }

    private static ToolCallStatus ToStatus(string? status, bool completed) => status switch
    {
        "inProgress" or "in_progress" or "running" or "pending" => ToolCallStatus.InProgress,
        "failed" or "error" => ToolCallStatus.Failed,
        "completed" or "success" or "done" => ToolCallStatus.Completed,
        _ => completed ? ToolCallStatus.Completed : ToolCallStatus.InProgress,
    };

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static long? Long(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
           && p.TryGetInt64(out var v)
            ? v
            : null;
}
