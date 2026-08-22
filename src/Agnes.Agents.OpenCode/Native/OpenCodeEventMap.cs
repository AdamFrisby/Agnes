using System.Text.Json;
using Agnes.Abstractions;

namespace Agnes.Agents.OpenCode.Native;

/// <summary>
/// Maps OpenCode's native session event stream onto Agnes events.
/// </summary>
/// <remarks>
/// The native server reports 28 event types where its ACP surface reports 6, and the extra ones are the
/// interesting ones: <c>step.failed</c> and <c>retried</c> name a failure the ACP path could only present as
/// a clean turn end, and <c>tool.success</c>/<c>tool.failed</c> distinguish outcomes ACP flattens into one
/// status. Everything here is pure over a parsed payload so the mapping is testable without a server.
/// </remarks>
public static class OpenCodeEventMap
{
    /// <summary>Turns one decoded SSE payload into zero or more Agnes events.</summary>
    public static IReadOnlyList<SessionEvent> ToEvents(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || payload.TryGetProperty("type", out var typeProp) is false
            || typeProp.GetString() is not { Length: > 0 } type)
        {
            return [];
        }

        var data = payload.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? d
            : default;

        return type switch
        {
            // Text and reasoning arrive as complete blocks, not deltas, so each is one event.
            "session.next.text.ended" =>
                Text(data) is { Length: > 0 } text
                    ? [new MessageChunkEvent(MessageRole.Assistant, new TextContent(text))]
                    : [],

            "session.next.reasoning.ended" =>
                Text(data) is { Length: > 0 } reasoning
                    ? [new ThoughtChunkEvent(new TextContent(reasoning))]
                    : [],

            "session.next.tool.called" =>
                [new ToolCallEvent(
                    Str(data, "callID") ?? string.Empty,
                    Str(data, "tool") ?? "tool",
                    ToolKindFor(Str(data, "tool")),
                    ToolCallStatus.InProgress,
                    [])],

            // ACP only ever said "completed"; here success and failure are distinct facts.
            "session.next.tool.success" =>
                [new ToolCallUpdateEvent(Str(data, "callID") ?? string.Empty, ToolCallStatus.Completed, null)],

            "session.next.tool.failed" =>
                [new ToolCallUpdateEvent(Str(data, "callID") ?? string.Empty, ToolCallStatus.Failed, null)],

            // The events the ACP path simply could not express.
            "session.next.step.failed" =>
                [new AgentErrorEvent(ErrorText(data) ?? "The agent's step failed.")],

            "session.next.retried" =>
                [new NoticeEvent(
                    $"The provider call failed and is being retried (attempt {Num(data, "attempt") ?? 0})"
                    + (ErrorText(data) is { Length: > 0 } why ? $": {why}" : "."))],

            "session.next.compaction.started" =>
                [new NoticeEvent("Compacting the conversation to free context…")],

            "session.next.context.updated" =>
                [new NoticeEvent($"Context updated: {Str(data, "text")}")],

            _ => [],
        };
    }

    /// <summary>Whether this mapper models an event type — so the caller can log what it drops rather than
    /// discarding it in silence, which is how the ACP path lost usage reporting for a whole session.</summary>
    public static bool IsKnown(string? type)
        => type is "session.next.text.ended" or "session.next.reasoning.ended"
            or "session.next.tool.called" or "session.next.tool.success" or "session.next.tool.failed"
            or "session.next.step.failed" or "session.next.retried"
            or "session.next.compaction.started" or "session.next.context.updated"
            // Modelled by deliberate omission: starts are paired with the ends we do map, and the rest
            // describe bookkeeping Agnes owns itself (prompt admission, reverts, moves).
            or "session.next.text.started" or "session.next.reasoning.started"
            or "session.next.tool.input.started" or "session.next.tool.input.ended"
            or "session.next.tool.progress" or "session.next.step.started" or "session.next.step.ended"
            or "session.next.prompted" or "session.next.prompt.admitted" or "session.next.synthetic"
            or "session.next.shell.started" or "session.next.shell.ended"
            or "session.next.compaction.ended" or "session.next.moved"
            or "session.next.agent.switched" or "session.next.model.switched"
            or "session.next.revert.staged" or "session.next.revert.cleared" or "session.next.revert.committed";

    /// <summary>OpenCode names its tools; Agnes's kinds drive the icon and colour a client shows.</summary>
    public static ToolKind ToolKindFor(string? tool) => tool switch
    {
        "read" => ToolKind.Read,
        "edit" or "write" or "patch" => ToolKind.Edit,
        "grep" or "glob" or "list" => ToolKind.Search,
        "bash" => ToolKind.Execute,
        "webfetch" or "websearch" => ToolKind.Fetch,
        "task" => ToolKind.Think,
        _ => ToolKind.Other,
    };

    private static string? Text(JsonElement data) => Str(data, "text");

    private static string? Str(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Num(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    /// <summary>Pulls a human-readable reason out of an error payload whose shape belongs to OpenCode.
    /// Kept tolerant on purpose: a failure we can only half-describe still beats reporting nothing.</summary>
    private static string? ErrorText(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("error", out var error))
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in (string[])["message", "name", "reason"])
        {
            if (error.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return error.GetRawText();
    }
}
