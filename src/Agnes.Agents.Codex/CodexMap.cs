using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Codex.Wire;

namespace Agnes.Agents.Codex;

/// <summary>
/// Maps the Codex app-server protocol (thread/turn/item notifications) to Agnes
/// <see cref="SessionEvent"/>s, and Agnes prompts/decisions the other way. Pure and golden-JSON
/// testable, like <c>ClaudeCodeStreamMapper</c> and the ACP mapper. Each inbound notification is
/// deserialized once into a typed <see cref="CodexItem"/> (etc.) and then matched on its <c>Type</c>,
/// so the mapping works with typed objects rather than hand-traversed JSON. One instance per session —
/// it remembers which message items streamed deltas (so a final <c>item/completed</c> doesn't duplicate
/// text) and which tool items already started (so a completion updates rather than re-adds), plus the
/// model's context window learned from token-usage notifications.
/// </summary>
internal sealed class CodexMap
{
    private readonly HashSet<string> _streamedMessages = [];
    private readonly HashSet<string> _startedTools = [];
    private long? _contextWindow;

    // ---- inbound: item lifecycle -> events ----

    /// <summary>An <c>item/started</c> notification: a tool call begins (messages stream via deltas).</summary>
    public IEnumerable<SessionEvent> ItemStarted(JsonElement notification)
        => ItemOf(notification) is { } item ? ToolStart(item) : [];

    /// <summary>An <c>item/completed</c> notification: final message text, or a tool call's result.</summary>
    public IEnumerable<SessionEvent> ItemCompleted(JsonElement notification)
    {
        if (ItemOf(notification) is not { } item)
        {
            yield break;
        }

        var id = item.Id ?? string.Empty;
        switch (item.Type)
        {
            case "agentMessage":
                // Skip if it already streamed via item/agentMessage/delta (avoid duplicating the text).
                if (!_streamedMessages.Contains(id) && item.Text is { Length: > 0 } text)
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
                foreach (var e in ToolComplete(item))
                {
                    yield return e;
                }

                break;
        }
    }

    /// <summary>An <c>item/agentMessage/delta</c> notification: a streamed chunk of assistant text.</summary>
    public SessionEvent? AgentMessageDelta(JsonElement notification)
    {
        if (TryDeserialize<CodexAgentMessageDeltaNotification>(notification) is { ItemId: { Length: > 0 } id, Delta: { Length: > 0 } delta })
        {
            _streamedMessages.Add(id);
            return new MessageChunkEvent(MessageRole.Assistant, new TextContent(delta));
        }

        return null;
    }

    /// <summary>A <c>thread/tokenUsage/updated</c> notification: real token usage (never estimated).</summary>
    public SessionEvent? TokenUsage(JsonElement notification)
    {
        if (TryDeserialize<CodexTokenUsageNotification>(notification)?.TokenUsage is not { } usage)
        {
            return null;
        }

        if (usage.ModelContextWindow is { } window)
        {
            _contextWindow = window;
        }

        // Context occupancy = the prompt-side tokens of the latest turn (fresh input + cached input).
        // Totals arrive nested under "total" or flattened onto the usage object; prefer the nested form.
        var input = usage.Total?.InputTokens ?? usage.InputTokens;
        var cached = usage.Total?.CachedInputTokens ?? usage.CachedInputTokens;
        long? context = input is null && cached is null ? null : (input ?? 0) + (cached ?? 0);
        var output = usage.Total?.OutputTokens ?? usage.OutputTokens;

        if (context is null && output is null)
        {
            return null;
        }

        return new UsageReportedEvent(new UsageMetrics(ContextUsed: context, ContextWindow: _contextWindow, OutputTokens: output));
    }

    // ---- tool items ----

    private IEnumerable<SessionEvent> ToolStart(CodexItem item)
    {
        if (item.Id is not { } id)
        {
            yield break;
        }

        if (item.Type == "subAgentActivity")
        {
            yield return new SubagentStartedEvent(item.AgentThreadId ?? id, item.Kind ?? "subagent");
            yield break;
        }

        _startedTools.Add(id);
        yield return new ToolCallEvent(id, ToolTitle(item), ToolKindFor(item.Type), ToolCallStatus.InProgress,
            [new TextContent(ToolDetail(item))]);
    }

    private IEnumerable<SessionEvent> ToolComplete(CodexItem item)
    {
        if (item.Id is not { } id || item.Type == "subAgentActivity")
        {
            yield break;
        }

        var status = ToStatus(item.Status, completed: true);
        var detail = ToolDetail(item);

        // If we saw the start, update it in place; otherwise surface it as a completed call.
        if (_startedTools.Remove(id))
        {
            yield return new ToolCallUpdateEvent(id, status, [new TextContent(detail)]);
        }
        else
        {
            yield return new ToolCallEvent(id, ToolTitle(item), ToolKindFor(item.Type), status, [new TextContent(detail)]);
        }
    }

    private static ToolKind ToolKindFor(string? type) => type switch
    {
        "commandExecution" => ToolKind.Execute,
        "fileChange" => ToolKind.Edit,
        "webSearch" => ToolKind.Fetch,
        "imageView" => ToolKind.Read,
        _ => ToolKind.Other,
    };

    private static string ToolTitle(CodexItem item) => item.Type switch
    {
        "commandExecution" => CommandText(item.Command) is { Length: > 0 } c ? c : "command",
        "fileChange" => FileChangeTitle(item),
        "webSearch" => item.Query ?? "web search",
        "mcpToolCall" or "dynamicToolCall" => item.Tool ?? "tool",
        "imageView" => item.Path ?? "image",
        _ => item.Type ?? string.Empty,
    };

    private static string ToolDetail(CodexItem item) => item.Type switch
    {
        "commandExecution" => item.AggregatedOutput ?? CommandText(item.Command) ?? string.Empty,
        "fileChange" => FileChangeSummary(item),
        "webSearch" => item.Query ?? string.Empty,
        "mcpToolCall" or "dynamicToolCall" => item.Arguments is { } a ? a.GetRawText() : string.Empty,
        _ => string.Empty,
    };

    // A command is a string, or an argv array of strings — the one genuinely polymorphic tool field.
    private static string? CommandText(JsonElement? command)
    {
        if (command is not { } c)
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

    private static string FileChangeTitle(CodexItem item)
    {
        var paths = ChangePaths(item).ToList();
        return paths.Count switch
        {
            0 => "file change",
            1 => paths[0],
            _ => $"{paths.Count} files",
        };
    }

    private static string FileChangeSummary(CodexItem item)
    {
        var sb = new StringBuilder();
        foreach (var path in ChangePaths(item))
        {
            sb.Append(path).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static IEnumerable<string> ChangePaths(CodexItem item)
        => item.Changes is null
            ? []
            : item.Changes.Where(c => !string.IsNullOrEmpty(c.Path)).Select(c => c.Path!);

    // Reasoning content is a string in some messages, an array of { text } blocks in others.
    private static string ReasoningText(CodexItem item)
    {
        if (item.Summary is { Length: > 0 } summary)
        {
            return summary;
        }

        if (item.Content is not { } content)
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    sb.Append(t.GetString());
                }
            }

            return sb.ToString();
        }

        return string.Empty;
    }

    private static IReadOnlyList<PlanEntry> PlanEntries(CodexItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return [];
        }

        var entries = new List<PlanEntry>();
        foreach (var raw in item.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // Recognize simple markdown checkboxes: "- [x]" (completed) vs "- [ ]" (pending).
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

    // ---- deserialize helpers ----

    // The notification's typed item, or null when the payload is absent/typeless (so an unknown or malformed
    // notification yields no events, matching the prior tolerant traversal).
    private static CodexItem? ItemOf(JsonElement notification)
    {
        var item = TryDeserialize<CodexItemNotification>(notification)?.Item;
        return item?.Type is { Length: > 0 } ? item : null;
    }

    // Boundary tolerance: the app-server's payloads are external, so a shape we don't expect must degrade to
    // "no events", never throw. STJ can throw on an unexpected field type, so a malformed frame maps to null.
    private static T? TryDeserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(CodexJson.Read);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static ToolCallStatus ToStatus(string? status, bool completed) => status switch
    {
        "inProgress" or "in_progress" or "running" or "pending" => ToolCallStatus.InProgress,
        "failed" or "error" => ToolCallStatus.Failed,
        "completed" or "success" or "done" => ToolCallStatus.Completed,
        _ => completed ? ToolCallStatus.Completed : ToolCallStatus.InProgress,
    };
}
