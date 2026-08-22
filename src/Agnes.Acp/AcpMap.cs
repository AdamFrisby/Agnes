using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Acp.Wire;

namespace Agnes.Acp;

/// <summary>Maps ACP wire values to Agnes domain types.</summary>
internal static class AcpMap
{
    public static ContentBlock ToContent(AcpContentBlock block) => block.Type switch
    {
        "text" => new TextContent(block.Text ?? string.Empty),
        "image" => new ImageContent(block.MimeType ?? "application/octet-stream", block.Data ?? string.Empty),
        "resource_link" => new ResourceLinkContent(block.Uri ?? string.Empty, block.Name),
        _ => new TextContent(block.Text ?? string.Empty),
    };

    public static AcpContentBlock FromContent(ContentBlock block) => block switch
    {
        TextContent t => new AcpContentBlock { Type = "text", Text = t.Text },
        ImageContent i => new AcpContentBlock { Type = "image", MimeType = i.MimeType, Data = i.Data },
        ResourceLinkContent r => new AcpContentBlock { Type = "resource_link", Uri = r.Uri, Name = r.Name },
        _ => new AcpContentBlock { Type = "text", Text = string.Empty },
    };

    public static ToolKind ToToolKind(string? kind) => kind switch
    {
        "read" => ToolKind.Read,
        "edit" => ToolKind.Edit,
        "delete" => ToolKind.Delete,
        "move" => ToolKind.Move,
        "search" => ToolKind.Search,
        "execute" => ToolKind.Execute,
        "think" => ToolKind.Think,
        "fetch" => ToolKind.Fetch,
        _ => ToolKind.Other,
    };

    public static ToolCallStatus ToToolStatus(string? status) => status switch
    {
        "pending" => ToolCallStatus.Pending,
        "in_progress" => ToolCallStatus.InProgress,
        "completed" => ToolCallStatus.Completed,
        "failed" => ToolCallStatus.Failed,
        _ => ToolCallStatus.Pending,
    };

    /// <summary>Narrows an ACP stop reason to the domain enum. Unknown values fall back to
    /// <see cref="StopReason.EndTurn"/> so a turn always terminates — but that fallback is indistinguishable
    /// from a real completion, so callers should check <see cref="IsKnownStopReason"/> and preserve the raw
    /// string (see <c>TurnEndedEvent.RawReason</c>) rather than let an unrecognised stop pass as success.</summary>
    public static StopReason ToStopReason(string? reason) => reason switch
    {
        "end_turn" => StopReason.EndTurn,
        "max_tokens" => StopReason.MaxTokens,
        "max_turn_requests" => StopReason.MaxTurnRequests,
        "refusal" => StopReason.Refusal,
        "cancelled" => StopReason.Cancelled,
        _ => StopReason.EndTurn,
    };

    /// <summary>Whether the agent's stop reason is one this protocol version models — i.e. whether
    /// <see cref="ToStopReason"/> mapped it or silently fell back.</summary>
    public static bool IsKnownStopReason(string? reason)
        => reason is "end_turn" or "max_tokens" or "max_turn_requests" or "refusal" or "cancelled";

    public static PermissionOptionKind ToOptionKind(string? kind) => kind switch
    {
        "allow_once" => PermissionOptionKind.AllowOnce,
        "allow_always" => PermissionOptionKind.AllowAlways,
        "reject_once" => PermissionOptionKind.RejectOnce,
        "reject_always" => PermissionOptionKind.RejectAlways,
        _ => PermissionOptionKind.AllowOnce,
    };

    /// <summary>
    /// Translate a single ACP <c>session/update</c> payload into zero or more Agnes events.
    /// Unknown variants are ignored (forward-compatible).
    /// </summary>
    public static IEnumerable<SessionEvent> ToEvents(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("sessionUpdate", out var kindProp))
        {
            yield break;
        }

        var kind = kindProp.GetString();
        switch (kind)
        {
            case "agent_message_chunk":
                yield return new MessageChunkEvent(MessageRole.Assistant, ContentOf(update));
                break;
            case "user_message_chunk":
                yield return new MessageChunkEvent(MessageRole.User, ContentOf(update));
                break;
            case "agent_thought_chunk":
                yield return new ThoughtChunkEvent(ContentOf(update));
                break;
            case "tool_call":
                yield return new ToolCallEvent(
                    GetString(update, "toolCallId") ?? string.Empty,
                    GetString(update, "title") ?? string.Empty,
                    ToToolKind(GetString(update, "kind")),
                    ToToolStatus(GetString(update, "status")),
                    ToolContentOf(update));
                break;
            case "tool_call_update":
                yield return new ToolCallUpdateEvent(
                    GetString(update, "toolCallId") ?? string.Empty,
                    update.TryGetProperty("status", out var s) ? ToToolStatus(s.GetString()) : null,
                    update.TryGetProperty("content", out _) ? ToolContentOf(update) : null);
                break;
            case "plan":
                yield return new PlanEvent(PlanEntriesOf(update));
                break;
            case "current_mode_update":
                yield return new ModeChangedEvent(GetString(update, "currentModeId") ?? string.Empty);
                break;
            case "usage_update":
                // OpenCode reports context occupancy and running cost this way. Dropping it (as this
                // mapper used to) is why an OpenCode session showed no token or cost figures at all
                // while a native-Claude one showed thousands.
                yield return new UsageReportedEvent(UsageOf(update));
                break;
            case "available_commands_update":
                // Deliberately not modelled: Agnes drives the agent, so its slash-command menu is noise.
                yield break;
            default:
                yield break;
        }
    }

    /// <summary>Whether this mapper models an update kind. Lets the caller log the ones it doesn't rather
    /// than dropping them silently — an agent quietly telling us something we never surface is exactly how
    /// the missing usage figures went unnoticed.</summary>
    public static bool IsKnownUpdateKind(string? kind)
        => kind is "agent_message_chunk" or "user_message_chunk" or "agent_thought_chunk"
            or "tool_call" or "tool_call_update" or "plan" or "current_mode_update"
            or "usage_update" or "available_commands_update";

    /// <summary>Reads ACP's usage shape: <c>used</c> (context tokens), <c>size</c> (the model's window)
    /// and <c>cost.amount</c>. Every field is optional and nothing is estimated — an absent number stays
    /// null rather than becoming a fabricated zero.</summary>
    private static UsageMetrics UsageOf(JsonElement update)
    {
        long? used = update.TryGetProperty("used", out var u) && u.TryGetInt64(out var usedValue) ? usedValue : null;
        long? size = update.TryGetProperty("size", out var z) && z.TryGetInt64(out var sizeValue) ? sizeValue : null;
        double? cost = update.TryGetProperty("cost", out var c)
                       && c.ValueKind == JsonValueKind.Object
                       && c.TryGetProperty("amount", out var amount)
                       && amount.TryGetDouble(out var costValue)
            ? costValue
            : null;

        return new UsageMetrics(ContextUsed: used, ContextWindow: size, CostUsd: cost);
    }

    private static ContentBlock ContentOf(JsonElement update)
    {
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            var block = content.Deserialize<AcpContentBlock>(AcpJson.CreateOptions());
            if (block is not null)
            {
                return ToContent(block);
            }
        }

        return new TextContent(string.Empty);
    }

    private static IReadOnlyList<ContentBlock> ToolContentOf(JsonElement update)
    {
        var result = new List<ContentBlock>();
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                // ACP tool-call content items: { type: "content", content: <block> } | { type: "diff", ... }
                var type = GetString(item, "type");
                if (type == "diff")
                {
                    result.Add(new DiffContent(
                        GetString(item, "path") ?? string.Empty,
                        GetString(item, "oldText"),
                        GetString(item, "newText") ?? string.Empty));
                }
                else if (item.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    var block = inner.Deserialize<AcpContentBlock>(AcpJson.CreateOptions());
                    if (block is not null)
                    {
                        result.Add(ToContent(block));
                    }
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<PlanEntry> PlanEntriesOf(JsonElement update)
    {
        var entries = new List<PlanEntry>();
        if (update.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                entries.Add(new PlanEntry(
                    GetString(e, "content") ?? string.Empty,
                    GetString(e, "status") ?? "pending",
                    GetString(e, "priority")));
            }
        }

        return entries;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
