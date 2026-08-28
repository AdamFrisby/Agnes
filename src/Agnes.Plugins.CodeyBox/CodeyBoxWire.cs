using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Plugins.CodeyBox;

// The orchestrator's REST shapes, as its live API actually returns them — several were confirmed against a
// running instance rather than read off the server's code, which is how the queue-status shape below was
// found to be `state`, not the `paused` flag it was first modelled as.
//
// Narrow local copies, not CodeyBox's own domain types: the coupling between the two products is REST+JSON
// and nothing else. Only what is rendered is modelled, so a field moving in CodeyBox breaks a compile here
// rather than a screen.

/// <summary>One work item in the queue.</summary>
public sealed record WorkItemRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("queuePosition")] long QueuePosition,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("lastError")] string? LastError,
    // Added after reading what a real queue holds: 404 items across three projects and six agents, 82 of
    // them with dependencies, one costing $73. None of that was modelled, so neither persona could answer
    // their first question without opening items one at a time.
    [property: JsonPropertyName("priority")] int Priority = 0,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt = default,
    [property: JsonPropertyName("dependsOnSatisfied")] bool DependsOnSatisfied = true,
    [property: JsonPropertyName("failureKind")] string? FailureKind = null,
    [property: JsonPropertyName("mergedPrNumber")] int? MergedPrNumber = null,
    [property: JsonPropertyName("mergedPrUrl")] string? MergedPrUrl = null,
    [property: JsonPropertyName("workBranch")] string? WorkBranch = null,
    [property: JsonPropertyName("usageTotal")] UsageTotal? UsageTotal = null)
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

    public string Age => Relative(UpdatedAt);

    /// <summary>
    /// Whether this item is waiting on a person or on a fix — the small fraction of a real queue that is
    /// actionable. On the instance this was designed against, 372 of 404 items were finished history.
    /// </summary>
    public bool NeedsAttention => IsFailed || State == "Queued" || !DependsOnSatisfied;

    /// <summary>What this item has cost, when the orchestrator has totalled it.</summary>
    public string? Cost => UsageTotal is { CostUsd: > 0 } u ? $"${u.CostUsd:0.00}" : null;

    public bool IsBlockedByDependency => !DependsOnSatisfied;

    public bool HasPr => MergedPrNumber is > 0;

    public string PrLabel => MergedPrNumber is { } n ? $"#{n}" : string.Empty;

    /// <summary>The one line under the title. Fixed order, so the eye learns where to look rather than
    /// re-reading each row.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { ShortId, State };
            if (Agent is { Length: > 0 }) { parts.Add(Agent); }
            if (ProjectId is { Length: > 0 }) { parts.Add(ProjectId); }
            if (Priority != 0) { parts.Add($"p{Priority}"); }
            if (Cost is { } cost) { parts.Add(cost); }
            if (HasPr) { parts.Add(PrLabel); }
            return string.Join("  ·  ", parts);
        }
    }

    internal static string Relative(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}

/// <summary>What an item has spent. Cost is the field an operator actually reads; the token counts are
/// there because the orchestrator reports them and they explain the cost.</summary>
public sealed record UsageTotal(
    [property: JsonPropertyName("costUsd")] decimal CostUsd,
    [property: JsonPropertyName("tokensInput")] long TokensInput,
    [property: JsonPropertyName("tokensOutput")] long TokensOutput);

/// <summary>
/// The queue's own state. <see cref="State"/> is a string the controller stringifies ("Running" /
/// "Paused"), <b>not</b> a boolean — and pausing takes a separate endpoint from resuming, each with its
/// own required body.
/// </summary>
public sealed record QueueStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("pausedAt")] DateTimeOffset? PausedAt,
    [property: JsonPropertyName("pausedReason")] string? PausedReason)
{
    public bool IsPaused => string.Equals(State, "Paused", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How much the orchestrator is running, and how much is waiting.</summary>
public sealed record WorkerStatus(
    [property: JsonPropertyName("maxConcurrent")] int MaxConcurrent,
    [property: JsonPropertyName("currentlyRunning")] int CurrentlyRunning,
    [property: JsonPropertyName("queuedCount")] int QueuedCount,
    [property: JsonPropertyName("lastSpawnAt")] DateTimeOffset? LastSpawnAt);

/// <summary>One project's row in the fleet view, including its budget position.</summary>
public sealed record FleetProject(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("queuedCount")] int QueuedCount,
    [property: JsonPropertyName("inFlightCount")] int InFlightCount,
    [property: JsonPropertyName("currentPhase")] string? CurrentPhase,
    [property: JsonPropertyName("isPaused")] bool IsPaused,
    [property: JsonPropertyName("hasRecentFailures")] bool HasRecentFailures,
    [property: JsonPropertyName("pausedReason")] string? PausedReason,
    [property: JsonPropertyName("monthlySpendUsd")] decimal MonthlySpendUsd,
    [property: JsonPropertyName("monthlyBudgetUsd")] decimal? MonthlyBudgetUsd,
    [property: JsonPropertyName("budgetThresholdState")] string? BudgetThresholdState)
{
    public string Spend => MonthlyBudgetUsd is { } budget
        ? $"${MonthlySpendUsd:0.##} / ${budget:0.##}"
        : $"${MonthlySpendUsd:0.##}";
}

/// <summary>An agent (or one instance of it) an operator has paused.</summary>
public sealed record AgentPause(
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("agentInstanceId")] string? AgentInstanceId,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("pausedAt")] DateTimeOffset? PausedAt,
    [property: JsonPropertyName("pausedReason")] string? PausedReason,
    [property: JsonPropertyName("pausedBy")] string? PausedBy);

/// <summary>A live agent session the supervision surface can watch and inject into.</summary>
public sealed record SupervisionSession(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("workItemId")] string? WorkItemId,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("outputTail")] string? OutputTail);

/// <summary>The supervision listing, which is paged.</summary>
public sealed record SupervisionSessions(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("sessions")] IReadOnlyList<SupervisionSession> Sessions);

/// <summary>The orchestrator's answer to an injection — accepted or not, and why not.</summary>
public sealed record InjectionReceipt(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("injectionId")] string? InjectionId,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>A suggestion the auditors raised out of a work item.</summary>
public sealed record Suggestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sourceWorkItemId")] string? SourceWorkItemId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("rationale")] string? Rationale,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("estimatedEffort")] string? EstimatedEffort,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("promotedToWorkItemId")] string? PromotedToWorkItemId)
{
    public string Age => WorkItemRow.Relative(CreatedAt);
}

/// <summary>The suggestions listing, which is paged.</summary>
public sealed record SuggestionPage(
    [property: JsonPropertyName("items")] IReadOnlyList<Suggestion> Items,
    [property: JsonPropertyName("total")] int Total);

/// <summary>A release the orchestrator is assembling.</summary>
public sealed record Release(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>A project the orchestrator runs work for.</summary>
public sealed record Project(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("repositoryUrl")] string? RepositoryUrl,
    [property: JsonPropertyName("defaultBaseBranch")] string? DefaultBaseBranch,
    [property: JsonPropertyName("defaultAgent")] string? DefaultAgent);

/// <summary>A task template that can be queued by name.</summary>
public sealed record TaskTemplate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("checkCount")] int CheckCount,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>An orchestrator-side plugin.</summary>
public sealed record OrchestratorPlugin(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>A chunk of an agent's stdout, as the hub broadcasts it.</summary>
public sealed record StdoutChunk(
    [property: JsonPropertyName("workItemId")] string WorkItemId,
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("chunk")] string Chunk);

/// <summary>
/// A question an agent has put to a human, and — once answered — the answer.
/// </summary>
/// <remarks>
/// This is an agent blocked on a person, which is the situation Agnes exists to unblock, so it is the one
/// part of the orchestrator's surface that earns a place in the queue view rather than behind a section.
/// The store behind it is optional: an orchestrator without one answers 503, which reads as "this instance
/// doesn't do questions" and not as a failure.
/// </remarks>
public sealed record WorkItemQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("workItemId")] string WorkItemId,
    [property: JsonPropertyName("questionId")] string QuestionId,
    [property: JsonPropertyName("questionText")] string QuestionText,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("askedAt")] DateTimeOffset AskedAt,
    [property: JsonPropertyName("answeredAt")] DateTimeOffset? AnsweredAt,
    [property: JsonPropertyName("answerText")] string? AnswerText,
    [property: JsonPropertyName("answeredBy")] string? AnsweredBy,
    [property: JsonPropertyName("dismissedAt")] DateTimeOffset? DismissedAt)
{
    /// <summary>Still waiting on a person — the only state that should interrupt anyone.</summary>
    public bool IsOpen => AnsweredAt is null && DismissedAt is null;

    public string Age => WorkItemRow.Relative(AskedAt);
}

/// <summary>What to create a work item from.</summary>
public sealed record NewWorkItem(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("baseBranch")] string? BaseBranch = null);

/// <summary>
/// A response this plugin passes through without modelling. Used for the orchestrator's diagnostic and
/// admin surfaces — timings, costs, diffs, capacity, quota, baselines, e2e runs — whose shapes are wide,
/// instance-specific and, on this host, sometimes unavailable entirely (several answer 503 when their
/// feature is off). Modelling them would be inventing a contract rather than reading one; the calls are
/// still typed and named, so nothing about the surface is hidden, only its interior left as JSON.
/// </summary>
public sealed record RawJson(JsonDocument Document)
{
    public string Text => Document.RootElement.ToString();
}
