using System.Text.Json.Serialization;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// One work item, as the orchestrator's REST API reports it.
/// </summary>
/// <remarks>
/// A narrow local copy, not CodeyBox's own <c>WorkItem</c>: the coupling between the two products is
/// REST + JSON and nothing else, and the real model carries a great deal this view never shows. Keeping
/// only what is rendered means a field moving in CodeyBox breaks a compile here rather than a screen.
/// </remarks>
public sealed record WorkItemRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("queuePosition")] long QueuePosition,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("lastError")] string? LastError)
{
    /// <summary>The id as CodeyBox's own tools abbreviate it.</summary>
    public string ShortId => Id.Length >= 8 ? Id[..8] : Id;

    /// <summary>Matches the orchestrator's terminal set — note that Merged is deliberately not terminal.</summary>
    public bool IsTerminal => State is "Done" or "Failed" or "Cancelled" or "AuditFailed"
        or "MergeConflictResolutionFailed" or "AbandonedAfterRecoveryAttempts";

    /// <summary>Whether this item is the one an operator would be watching.</summary>
    public bool IsActive => State is "Working" or "Auditing" or "Reworking" or "Merging";

    public bool IsFailed => State is "Failed" or "AuditFailed"
        or "MergeConflictResolutionFailed" or "AbandonedAfterRecoveryAttempts";

    public string Age
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - UpdatedAt;
            if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }
}

/// <summary>The queue's own state, separate from any one item's.</summary>
public sealed record QueueStatus(
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>A chunk of an agent's stdout, as the hub broadcasts it.</summary>
public sealed record StdoutChunk(
    [property: JsonPropertyName("workItemId")] string WorkItemId,
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("chunk")] string Chunk);
