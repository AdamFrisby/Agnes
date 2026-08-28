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
    // The task as it was given to the agent. The single most useful thing about an item and it was not
    // modelled at all, so the pane could show a title and nothing of what the work actually is.
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<string>? DependsOn = null,
    [property: JsonPropertyName("priority")] int Priority = 0,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt = default,
    [property: JsonPropertyName("dependsOnSatisfied")] bool DependsOnSatisfied = true,
    [property: JsonPropertyName("failureKind")] string? FailureKind = null,
    [property: JsonPropertyName("mergedPrNumber")] int? MergedPrNumber = null,
    [property: JsonPropertyName("mergedPrUrl")] string? MergedPrUrl = null,
    [property: JsonPropertyName("workBranch")] string? WorkBranch = null,
    [property: JsonPropertyName("usageTotal")] UsageTotal? UsageTotal = null,
    // Added after the second pane audit. Each of these answers a question the pane was being asked and
    // could not answer; each is populated on this instance (counts in docs/codeybox-item-pane-audit.md).
    // Quota retries are why an item can be stopped without being stuck — 57 items here have been through
    // one. Without them, "waiting for the provider window to reopen" and "wedged" look identical.
    [property: JsonPropertyName("quotaRetryAttempts")] int QuotaRetryAttempts = 0,
    [property: JsonPropertyName("quotaResetAt")] DateTimeOffset? QuotaResetAt = null,
    [property: JsonPropertyName("nextQuotaRetryAt")] DateTimeOffset? NextQuotaRetryAt = null,
    [property: JsonPropertyName("nextTransientRetryAt")] DateTimeOffset? NextTransientRetryAt = null,
    [property: JsonPropertyName("transientRetryAttempts")] int TransientRetryAttempts = 0,
    [property: JsonPropertyName("upstreamPushAttempts")] int UpstreamPushAttempts = 0,
    // Only 6 of the 50 cancellations here were an operator's. The rest the orchestrator made itself,
    // which is a materially different fact to report.
    [property: JsonPropertyName("cancellationSource")] string? CancellationSource = null,
    [property: JsonPropertyName("externalId")] string? ExternalId = null,
    [property: JsonPropertyName("repositoryUrl")] string? RepositoryUrl = null)
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

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

    public bool HasError => !string.IsNullOrWhiteSpace(LastError) || !string.IsNullOrWhiteSpace(FailureKind);

    /// <summary>
    /// What to call the box carrying <see cref="ErrorSummary"/>. Not always "Failure": of the 67 items
    /// here that carry an error, 45 are Cancelled rather than Failed, and only 6 of the 50 cancellations
    /// were an operator's. Heading all of those "Failure" in the danger hue overstates two-thirds of them
    /// and spends the colour that is supposed to mean the other third.
    /// </summary>
    public string ErrorTitle => State switch
    {
        "Cancelled" when CancellationSource is { Length: > 0 } src
            && src.Equals("operator", StringComparison.OrdinalIgnoreCase) => "Cancelled by an operator",
        "Cancelled" => "Cancelled by the orchestrator",
        _ => "Failure",
    };

    /// <summary>Whether the error box should wear the danger hue, as opposed to reporting a fact about an
    /// item nobody is being asked to fix.</summary>
    public bool ErrorIsFailure => State != "Cancelled";

    /// <summary>
    /// Stopped but not stuck. The orchestrator backs off and resumes on its own for provider quota and
    /// transient faults; without saying so, an item mid-backoff looks exactly like one that has died, and
    /// the operator retries something that was already going to retry itself.
    /// </summary>
    public bool IsWaiting => !IsTerminal
        && (QuotaRetryAttempts > 0 || TransientRetryAttempts > 0
            || NextQuotaRetryAt is not null || NextTransientRetryAt is not null || QuotaResetAt is not null);

    public string WaitingSummary
    {
        get
        {
            var parts = new List<string>();
            if (QuotaRetryAttempts > 0)
            {
                parts.Add($"{QuotaRetryAttempts} quota retr{(QuotaRetryAttempts == 1 ? "y" : "ies")}");
            }

            if (TransientRetryAttempts > 0)
            {
                parts.Add($"{TransientRetryAttempts} transient retr{(TransientRetryAttempts == 1 ? "y" : "ies")}");
            }

            // Only ever one resume time is reported, and the soonest is the one that governs.
            var resume = new[] { NextQuotaRetryAt, NextTransientRetryAt, QuotaResetAt }
                .Where(t => t is not null)
                .Select(t => t!.Value)
                .DefaultIfEmpty()
                .Min();

            if (resume != default)
            {
                parts.Add(resume > DateTimeOffset.UtcNow
                    ? $"resumes {resume.ToLocalTime():MMM d HH:mm}"
                    : $"was due {resume.ToLocalTime():MMM d HH:mm}");
            }

            return parts.Count == 0 ? "Waiting to resume" : string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// The first few lines of the task. The full prompt has a median length of 2,726 characters here and
    /// runs to 10,207, so showing it whole pushed the live output — the only thing on the pane that
    /// moves — several screens down.
    /// </summary>
    public string PromptPreview
    {
        get
        {
            if (Prompt is not { Length: > 0 } prompt)
            {
                return string.Empty;
            }

            var trimmed = prompt.Trim();
            return trimmed.Length <= PromptPreviewLength
                ? trimmed
                : trimmed[..PromptPreviewLength].TrimEnd() + "…";
        }
    }

    private const int PromptPreviewLength = 260;

    /// <summary>Whether there is more task than the preview shows, i.e. whether an expander is warranted.</summary>
    public bool PromptIsTruncated => (Prompt?.Trim().Length ?? 0) > PromptPreviewLength;

    public bool HasExternalId => !string.IsNullOrWhiteSpace(ExternalId);

    /// <summary>Where the merged PR can be opened, preferring the URL the orchestrator recorded and
    /// falling back to composing one from the repository.</summary>
    public string? PrUrl => MergedPrUrl is { Length: > 0 } url
        ? url
        : (RepositoryUrl is { Length: > 0 } repo && MergedPrNumber is { } n
            ? repo.TrimEnd('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase) + "/pull/" + n
            : null);

    public bool HasPrLink => PrUrl is { Length: > 0 };

    public string PriorityLabel => Priority.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string CostLabel => Cost ?? "—";

    public string ProjectLabel => ProjectId is { Length: > 0 } p ? p : "—";

    public string AgentLabel => Agent is { Length: > 0 } a ? a : "—";

    /// <summary>The failure in one line: its kind when the orchestrator classified it, then the message.</summary>
    public string ErrorSummary => (FailureKind, LastError) switch
    {
        ({ Length: > 0 } kind, { Length: > 0 } error) => $"{kind} — {error}",
        ({ Length: > 0 } kind, _) => kind,
        (_, { Length: > 0 } error) => error,
        _ => string.Empty,
    };

    public bool HasDependencies => DependsOn is { Count: > 0 };

    /// <summary>What this item is waiting on, and whether that wait is over.</summary>
    public string DependencySummary => DependsOn is { Count: > 0 } deps
        ? $"{deps.Count} " + (deps.Count == 1 ? "dependency" : "dependencies") +
          (DependsOnSatisfied ? " · satisfied" : " · NOT satisfied")
        : string.Empty;

    public bool HasBranch => !string.IsNullOrWhiteSpace(WorkBranch);

    /// <summary>The identity line under the title: where this item lives and what ran it.</summary>
    public string Provenance
    {
        get
        {
            var parts = new List<string> { ShortId };
            if (ProjectId is { Length: > 0 }) { parts.Add(ProjectId); }
            if (Agent is { Length: > 0 }) { parts.Add(Agent); }
            if (Priority != 0) { parts.Add($"priority {Priority}"); }
            if (Cost is { } cost) { parts.Add(cost); }
            if (HasBranch) { parts.Add(WorkBranch!); }
            if (HasPr) { parts.Add($"PR {PrLabel}"); }
            return string.Join("  ·  ", parts);
        }
    }

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

/// <summary>
/// One thing that happened to a work item, as the orchestrator's audit log records it.
/// </summary>
/// <remarks>
/// <para>The seven kinds the reader emits are <c>state_transition</c>, <c>agent_started</c>,
/// <c>agent_finished</c>, <c>agent_stuck</c>, <c>auditor_run</c>, <c>iteration_complete</c> and
/// <c>webhook_delivered</c>, mapped from eighteen source events. This is the record of how an item got to
/// where it is — which audit iteration failed, which auditor objected, whether the agent was killed by
/// the stuck probe — and it is the answer to "why is this like this".</para>
///
/// <para><see cref="Details"/> stays <see cref="JsonElement"/> deliberately: its shape is per-kind
/// (an auditor run carries name/severity/duration, an iteration carries blocking and non-blocking counts),
/// so this is a genuinely polymorphic sub-field at an external boundary rather than laziness. What the UI
/// reads out of it is named below.</para>
/// </remarks>
public sealed record TimelineEntry(
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("details")] JsonElement Details)
{
    public string At => OccurredAt.ToLocalTime().ToString("MMM d HH:mm:ss");

    public string Age => WorkItemRow.Relative(OccurredAt);

    /// <summary>The kind in the operator's words rather than the log's.</summary>
    public string Label => Kind switch
    {
        "state_transition" => "State",
        "agent_started" => "Agent started",
        "agent_finished" => "Agent finished",
        "agent_stuck" => "Agent stuck",
        "auditor_run" => "Auditor",
        "iteration_complete" => "Audit iteration",
        "webhook_delivered" => "Webhook",
        _ => Kind,
    };

    /// <summary>Whether this entry is one of the bad ones — the rows worth finding in a long timeline.</summary>
    public bool IsTrouble => Kind is "agent_stuck" ||
        Summary.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        Summary.Contains("blocking", StringComparison.OrdinalIgnoreCase);

    /// <summary>The audit iteration this belongs to, when the entry names one.</summary>
    public int? Iteration => Details.ValueKind == JsonValueKind.Object &&
        Details.TryGetProperty("iteration", out var i) && i.ValueKind == JsonValueKind.Number
            ? i.GetInt32()
            : null;

    public bool HasIteration => Iteration is not null;

    public string IterationLabel => Iteration is { } i ? $"iter {i}" : string.Empty;
}

/// <summary>A work item's timeline, as the endpoint returns it.</summary>
public sealed record WorkItemTimeline(
    [property: JsonPropertyName("workItemId")] string WorkItemId,
    [property: JsonPropertyName("entries")] IReadOnlyList<TimelineEntry> Entries);

/// <summary>
/// One agent run against a work item: which agent, which model, which phase, and how it ended.
/// </summary>
/// <remarks>
/// This is the orchestrator's own record, out of its database, and it is the honest source for "what
/// happened to this item". The admin UI reconstructs a timeline by scraping the audit logs instead, which
/// is both weaker and lossy: those logs roll daily, so on this instance every item's scraped timeline came
/// back <b>empty</b> while this endpoint returned seventy-two runs for the same item — model fallbacks and
/// all.
/// </remarks>
public sealed record AgentRun(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agentKind")] string AgentKind,
    [property: JsonPropertyName("modelId")] string? ModelId,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("endedAt")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("iteration")] int? Iteration,
    [property: JsonPropertyName("outcome")] string? Outcome)
{
    public string At => StartedAt.ToLocalTime().ToString("MMM d HH:mm");

    /// <summary>Anything that is not plain success — the rows worth finding in a run of seventy.</summary>
    public bool Failed => Outcome is { Length: > 0 } o && !o.Equals("success", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The audit gates are named <c>audit:security:llm-review</c> and the like — seventeen of them on one
    /// item. Only the <c>audit:</c> prefix is dropped, because it is the same for every gate while the
    /// rest is what tells them apart: five of them end in <c>llm-review</c>, so trimming to the last
    /// segment rendered security, architecture, quality, completeness and cheating identically.
    /// </summary>
    public string PhaseLabel => Phase.StartsWith("audit:", StringComparison.Ordinal)
        ? Phase["audit:".Length..]
        : Phase;

    public bool IsAudit => Phase.StartsWith("audit:", StringComparison.Ordinal);

    public string Duration => EndedAt is { } ended
        ? Humanise(ended - StartedAt)
        : "running";

    public string IterationLabel => Iteration is { } i ? $"iter {i}" : string.Empty;

    public bool HasIteration => Iteration is not null;

    /// <summary>Agent and model together: the fallback chain is only legible when both are shown.</summary>
    public string Ran => ModelId is { Length: > 0 } model ? $"{AgentKind} · {model}" : AgentKind;

    internal static string Humanise(TimeSpan span) => span.TotalHours >= 1
        ? $"{span.TotalHours:0.#}h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0}m" : $"{span.TotalSeconds:0}s";
}

/// <summary>Every agent run against one item, newest last.</summary>
public sealed record AgentHistory(
    [property: JsonPropertyName("workAgent")] string? WorkAgent,
    [property: JsonPropertyName("agentHistory")] IReadOnlyList<AgentRun> Runs);

/// <summary>How long one phase took, and what it cost — the two questions asked of a finished item.</summary>
public sealed record PhaseSummary(string Phase, long DurationMs, decimal CostUsd)
{
    public string Duration => AgentRun.Humanise(TimeSpan.FromMilliseconds(DurationMs));

    public string Cost => CostUsd > 0 ? $"${CostUsd:0.00}" : string.Empty;

    public bool HasCost => CostUsd > 0;
}

/// <summary>One thing an auditor objected to: what, how badly, and where.</summary>
public sealed record AuditFinding(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("files")] IReadOnlyList<string>? Files,
    [property: JsonPropertyName("lineHints")] IReadOnlyList<string>? LineHints)
{
    /// <summary>Error is what actually blocks a merge; everything else is advice.</summary>
    public bool IsBlocking => string.Equals(Severity, "Error", StringComparison.OrdinalIgnoreCase);

    public bool HasFiles => Files is { Count: > 0 };

    public string FileList => Files is { Count: > 0 } f ? string.Join(", ", f) : string.Empty;
}

/// <summary>One auditor's verdict for one iteration.</summary>
public sealed record AuditAuditor(
    [property: JsonPropertyName("auditorName")] string AuditorName,
    [property: JsonPropertyName("auditorKind")] string? AuditorKind,
    [property: JsonPropertyName("worstSeverity")] string? WorstSeverity,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("findings")] IReadOnlyList<AuditFinding>? Findings,
    [property: JsonPropertyName("rawOutputAvailable")] bool RawOutputAvailable)
{
    public IReadOnlyList<AuditFinding> All => Findings ?? [];

    public bool HasFindings => All.Count > 0;

    public bool Objected => All.Any(f => f.IsBlocking);

    public string Verdict => All.Count == 0
        ? "passed"
        : $"{All.Count(f => f.IsBlocking)} blocking, {All.Count(f => !f.IsBlocking)} advisory";

    public string Duration => AgentRun.Humanise(TimeSpan.FromMilliseconds(DurationMs));
}

/// <summary>
/// One audit iteration: every auditor that ran, and what each said.
/// </summary>
/// <remarks>
/// This is the direct answer to "why did that round fail" — which gate objected, to what, in which files.
/// Plan and code iterations are counted separately, so <see cref="Target"/> is part of the identity rather
/// than decoration.
/// </remarks>
public sealed record AuditIteration(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("iteration")] int Iteration,
    [property: JsonPropertyName("blockingCount")] int BlockingCount,
    [property: JsonPropertyName("nonBlockingCount")] int NonBlockingCount,
    [property: JsonPropertyName("auditors")] IReadOnlyList<AuditAuditor>? Auditors)
{
    public IReadOnlyList<AuditAuditor> All => Auditors ?? [];

    public bool Blocked => BlockingCount > 0;

    public string Header => $"{Target} · iteration {Iteration}";

    public string Verdict => BlockingCount > 0
        ? $"{BlockingCount} blocking · {NonBlockingCount} advisory"
        : NonBlockingCount > 0 ? $"passed · {NonBlockingCount} advisory" : "passed";
}

/// <summary>Every audit iteration recorded against one item.</summary>
public sealed record AuditReports(
    [property: JsonPropertyName("workItemId")] string WorkItemId,
    [property: JsonPropertyName("iterations")] IReadOnlyList<AuditIteration>? Iterations);

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
