using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Talks to one CodeyBox orchestrator: REST for the queue, SignalR for an agent's live stdout.
/// </summary>
/// <remarks>
/// <para>Its own <see cref="HttpClient"/>, deliberately — <b>not</b> Agnes's <c>AgnesHttp.For(pin)</c>.
/// That exists because an Agnes host is typically self-signed and authenticated by a pinned certificate
/// fingerprint. CodeyBox is a different service with different trust rules (a bearer key over plain HTTP,
/// usually on localhost), so it takes a client of its own; <c>GitHubDeviceLogin</c> is the existing
/// precedent for one flow reaching two services that are trusted differently.</para>
///
/// <para>The hub carries the same bearer token as the REST calls. Its callback is <c>stdoutChunk</c>,
/// scoped to a work item by <c>SubscribeAsync</c> — the server puts the connection in a <c>wi:{id}</c>
/// group, so no filtering is needed here beyond following the right item.</para>
/// </remarks>
public sealed class CodeyBoxClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CodeyBoxOptions _options;
    private readonly HttpClient _http;

    private HubConnection? _hub;
    private string? _following;

    public CodeyBoxClient(CodeyBoxOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(options.BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        if (options.ApiKey is { Length: > 0 } key)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    /// <summary>Raised for each chunk of the followed item's agent output.</summary>
    public event Action<StdoutChunk>? StdoutReceived;

    /// <summary>Raised when the followed item's stream ends.</summary>
    public event Action<string>? StreamCompleted;

    public async Task<IReadOnlyList<WorkItemRow>> ListWorkItemsAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<List<WorkItemRow>>("workitems", Json, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<QueueStatus?> GetQueueStatusAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<QueueStatus>("queue/status", Json, cancellationToken).ConfigureAwait(false);

    public async Task<WorkerStatus?> GetWorkerStatusAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<WorkerStatus>("workers/status", Json, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<FleetProject>> GetFleetAsync(CancellationToken cancellationToken = default)
        => await Get<List<FleetProject>>("fleet/summary", cancellationToken).ConfigureAwait(false) ?? [];

    public Task<RawJson?> GetFleetTransitionHealthAsync(CancellationToken cancellationToken = default)
        => GetRaw("fleet/transition-health", cancellationToken);

    public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken cancellationToken = default)
        => await Get<List<Project>>("projects", cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TaskTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
        => await Get<List<TaskTemplate>>("templates", cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<OrchestratorPlugin>> GetPluginsAsync(CancellationToken cancellationToken = default)
        => await Get<List<OrchestratorPlugin>>("plugins", cancellationToken).ConfigureAwait(false) ?? [];

    // ---- agents ----

    public async Task<IReadOnlyList<AgentPause>> GetPausedAgentsAsync(CancellationToken cancellationToken = default)
        => await Get<List<AgentPause>>("agents/paused", cancellationToken).ConfigureAwait(false) ?? [];

    public Task PauseAgentAsync(string kind, string reason, CancellationToken cancellationToken = default)
        => PostJsonAsync($"agents/{kind}/pause", new { reason }, cancellationToken);

    public Task ResumeAgentAsync(string kind, CancellationToken cancellationToken = default)
        => PostJsonAsync($"agents/{kind}/resume", new { }, cancellationToken);

    public Task PauseAgentInstanceAsync(string kind, string instanceId, string reason, CancellationToken cancellationToken = default)
        => PostJsonAsync($"agents/{kind}/instances/{instanceId}/pause", new { reason }, cancellationToken);

    public Task ResumeAgentInstanceAsync(string kind, string instanceId, CancellationToken cancellationToken = default)
        => PostJsonAsync($"agents/{kind}/instances/{instanceId}/resume", new { }, cancellationToken);

    public Task<RawJson?> GetAgentPricingAsync(CancellationToken cancellationToken = default)
        => GetRaw("agent-pricing", cancellationToken);

    // ---- supervision: watching a live agent, and speaking into it ----

    public async Task<SupervisionSessions?> GetSupervisionSessionsAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<SupervisionSessions>(
            "agent-supervision/sessions", Json, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Sends a message into a running agent's session. The orchestrator answers with a receipt rather than
    /// a status code alone, because an injection can be legitimately refused — the session may have moved
    /// on — and that is not the same as the call failing.
    /// </summary>
    public async Task<InjectionReceipt?> InjectAsync(string sessionId, string message, string? actor = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"agent-supervision/sessions/{sessionId}/injections")
        {
            Content = JsonContent.Create(new { message, actor }, options: Json),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InjectionReceipt>(Json, cancellationToken).ConfigureAwait(false);
    }

    // ---- suggestions ----

    public async Task<SuggestionPage?> GetSuggestionsAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<SuggestionPage>("suggestions", Json, cancellationToken).ConfigureAwait(false);

    public Task<RawJson?> GetSuggestionAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"suggestions/{id}", cancellationToken);

    public Task PromoteSuggestionAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"suggestions/{id}/promote", new { }, cancellationToken);

    public Task DismissSuggestionAsync(string id, string reason, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"suggestions/{id}")
        {
            Content = JsonContent.Create(new { state = "Dismissed", dismissReason = reason }, options: Json),
        }, cancellationToken);

    // ---- releases ----

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellationToken = default)
        => await Get<List<Release>>("releases", cancellationToken).ConfigureAwait(false) ?? [];

    public Task<RawJson?> GetReleaseAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"releases/{id}", cancellationToken);

    public async Task<IReadOnlyList<WorkItemRow>> GetReleaseWorkItemsAsync(string id, CancellationToken cancellationToken = default)
        => await Get<List<WorkItemRow>>($"releases/{id}/workitems", cancellationToken).ConfigureAwait(false) ?? [];

    public Task CloseReleaseAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"releases/{id}/close", new { }, cancellationToken);

    public Task ReopenReleaseAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"releases/{id}/reopen", new { }, cancellationToken);

    public Task AbandonReleaseAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"releases/{id}/abandon", new { }, cancellationToken);

    public Task ShipReleaseAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"releases/{id}/release", new { }, cancellationToken);

    // ---- diagnostics ----
    // Wide, instance-specific, and on some hosts switched off entirely (capacity and quota answer 503 when
    // their feature is unavailable). Typed as calls, left as JSON inside — see RawJson.

    public Task<RawJson?> GetCapacityAsync(CancellationToken cancellationToken = default)
        => GetRaw("stats/capacity", cancellationToken);

    public Task<RawJson?> GetQuotaHistoryAsync(CancellationToken cancellationToken = default)
        => GetRaw("quota/history", cancellationToken);

    public Task<RawJson?> GetQuotaResetAdviceAsync(CancellationToken cancellationToken = default)
        => GetRaw("quota/reset-advice", cancellationToken);

    public Task<RawJson?> GetQuotaResetCreditsAsync(CancellationToken cancellationToken = default)
        => GetRaw("quota/reset-credits", cancellationToken);

    public Task<RawJson?> GetQuotaRetryStatusAsync(CancellationToken cancellationToken = default)
        => GetRaw("admin/quota-retry-status", cancellationToken);

    public Task<RawJson?> GetSandboxLeaksAsync(CancellationToken cancellationToken = default)
        => GetRaw("admin/sandbox-leaks", cancellationToken);

    public Task<RawJson?> GetSandboxResourceUsageAsync(CancellationToken cancellationToken = default)
        => GetRaw("admin/sandbox-resource-usage", cancellationToken);

    public Task<RawJson?> GetLeakedSandboxesAsync(CancellationToken cancellationToken = default)
        => GetRaw("sandboxes/leaked", cancellationToken);

    public Task DisposeLeakedSandboxAsync(string name, CancellationToken cancellationToken = default)
        => PostJsonAsync($"sandboxes/leaked/{name}/dispose", new { }, cancellationToken);

    public Task<RawJson?> GetBaselinesAsync(CancellationToken cancellationToken = default)
        => GetRaw("baselines", cancellationToken);

    public Task<RawJson?> GetE2eRunsAsync(CancellationToken cancellationToken = default)
        => GetRaw("e2eruns", cancellationToken);

    public Task<RawJson?> GetTestCasesAsync(CancellationToken cancellationToken = default)
        => GetRaw("testcases", cancellationToken);

    public Task<RawJson?> GetWorkersAsync(CancellationToken cancellationToken = default)
        => GetRaw("workers", cancellationToken);

    // ---- per-work-item detail ----

    public Task<RawJson?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}", cancellationToken);

    /// <summary>
    /// How this item got to where it is. Empty is an ordinary answer, not a failure: the timeline is read
    /// back out of the orchestrator's audit logs, which roll daily, so an item older than the retained
    /// window has none left — on the instance this was built against, every item returned zero.
    /// </summary>
    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(string id, CancellationToken cancellationToken = default)
    {
        var timeline = await Get<WorkItemTimeline>($"workitems/{id}/timeline", cancellationToken).ConfigureAwait(false);
        return timeline?.Entries ?? [];
    }

    /// <summary>
    /// Every agent run against this item, from the orchestrator's database. The real answer to "what
    /// happened here" — richer and more durable than the log-scraped timeline, which rolls away.
    /// </summary>
    public async Task<IReadOnlyList<AgentRun>> GetAgentRunsAsync(string id, CancellationToken cancellationToken = default)
    {
        var history = await Get<AgentHistory>($"workitems/{id}/agent-history", cancellationToken).ConfigureAwait(false);
        return history?.Runs ?? [];
    }

    /// <summary>
    /// Per-phase duration and cost, joined from the two endpoints that each hold half of it.
    /// </summary>
    public async Task<IReadOnlyList<PhaseSummary>> GetPhaseSummaryAsync(string id, CancellationToken cancellationToken = default)
    {
        var timings = await GetRaw($"workitems/{id}/timings", cancellationToken).ConfigureAwait(false);
        var costs = await GetRaw($"workitems/{id}/costs", cancellationToken).ConfigureAwait(false);

        var durations = ReadPhases(timings, "byPhase", "durationMs");
        var spend = ReadPhases(costs, "byPhase", "estimatedUsd");

        return [.. durations.Keys.Union(spend.Keys)
            .Select(phase => new PhaseSummary(phase, (long)durations.GetValueOrDefault(phase), spend.GetValueOrDefault(phase)))
            .Where(p => p.DurationMs > 0 || p.CostUsd > 0)
            .OrderByDescending(p => p.DurationMs)];
    }

    private static Dictionary<string, decimal> ReadPhases(RawJson? json, string container, string field)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (json?.Document.RootElement.TryGetProperty(container, out var phases) != true ||
            phases.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var phase in phases.EnumerateObject())
        {
            if (phase.Value.ValueKind == JsonValueKind.Object &&
                phase.Value.TryGetProperty(field, out var value) &&
                value.ValueKind == JsonValueKind.Number)
            {
                result[phase.Name] = value.GetDecimal();
            }
        }

        return result;
    }

    public Task<RawJson?> GetAgentHistoryAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/agent-history", cancellationToken);

    public Task<RawJson?> GetCostsAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/costs", cancellationToken);

    public Task<RawJson?> GetTimingsAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/timings", cancellationToken);

    public Task<RawJson?> GetDiffAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/diff", cancellationToken);

    /// <summary>
    /// The questions an agent has asked about this item. Empty when the orchestrator has no question store
    /// configured — it answers 503 for that, which is a statement about the instance, not an error.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemQuestion>> GetQuestionsAsync(string id, CancellationToken cancellationToken = default)
        => await Get<List<WorkItemQuestion>>($"workitems/{id}/questions", cancellationToken).ConfigureAwait(false) ?? [];

    public Task<RawJson?> GetDependentsAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/dependents", cancellationToken);

    /// <summary>
    /// What each auditor objected to, per iteration — the direct answer to "why did that round fail".
    /// </summary>
    /// <remarks>
    /// Empty is a real answer and not a failure: the report store was empty for all 404 items on the
    /// instance this was built against, so the UI says so rather than showing a blank panel. The shape
    /// below is the endpoint's own, read from its DTOs.
    /// </remarks>
    public async Task<IReadOnlyList<AuditIteration>> GetAuditIterationsAsync(string id, CancellationToken cancellationToken = default)
    {
        var reports = await Get<AuditReports>($"workitems/{id}/audit-reports", cancellationToken).ConfigureAwait(false);
        return reports?.Iterations ?? [];
    }

    public Task<RawJson?> GetAuditReportsAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/audit-reports", cancellationToken);

    public Task<RawJson?> GetAgentStreamsAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/agent-streams", cancellationToken);

    public Task<RawJson?> GetAttachmentsAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/attachments", cancellationToken);

    // ---- work-item lifecycle ----

    public async Task<string?> CreateWorkItemAsync(NewWorkItem item, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "workitems")
        {
            Content = JsonContent.Create(item, options: Json),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public Task AbandonAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"workitems/{id}/abandon", new { }, cancellationToken);

    public Task UncancelAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"workitems/{id}/uncancel", new { }, cancellationToken);

    public Task ResumeWorkItemAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"workitems/{id}/resume", new { }, cancellationToken);

    public Task RecoverAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"workitems/{id}/recover", new { }, cancellationToken);

    public Task ReplayAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"workitems/{id}/replay", new { }, cancellationToken);

    public Task SetPriorityAsync(string id, int priority, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"workitems/{id}/priority")
        {
            Content = JsonContent.Create(new { priority }, options: Json),
        }, cancellationToken);

    public Task SetPromptAsync(string id, string prompt, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Put, $"workitems/{id}/prompt")
        {
            Content = JsonContent.Create(new { prompt }, options: Json),
        }, cancellationToken);

    public Task AnswerQuestionAsync(string id, string questionId, string answer, CancellationToken cancellationToken = default)
        => PostJsonAsync($"workitems/{id}/answer", new { questionId, answer }, cancellationToken);

    /// <summary>Dismisses a question. The orchestrator requires both the id and a reason, and validates the
    /// id against <c>^[a-zA-Z0-9_-]{1,64}$</c> before it will act.</summary>
    public Task DismissQuestionAsync(string id, string questionId, string reason, CancellationToken cancellationToken = default)
        => PostJsonAsync($"workitems/{id}/dismiss-question", new { questionId, reason }, cancellationToken);

    public Task ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default)
        => PostJsonAsync("workitems/reorder", new { ids = orderedIds }, cancellationToken);

    public Task QueueTemplateAsync(string name, CancellationToken cancellationToken = default)
        => PostJsonAsync($"templates/{name}/queue", new { }, cancellationToken);

    // ---- the rest of the surface ----
    // Completing the map so nothing is unreachable. Two families are deliberately absent and are not gaps:
    // `github-app/callback` and `webhooks/github/release` are inbound endpoints the orchestrator exposes
    // for GitHub to call, not actions a client performs.

    public Task<RawJson?> GetProjectAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"projects/{id}", cancellationToken);

    public Task<RawJson?> GetProjectBudgetAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"projects/{id}/budget", cancellationToken);

    public Task<RawJson?> GetProjectBudgetUsageAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"projects/{id}/budget/usage", cancellationToken);

    public Task PauseProjectQueueAsync(string id, string reason, CancellationToken cancellationToken = default)
        => PostJsonAsync($"projects/{id}/queue/pause", new { reason }, cancellationToken);

    public Task ResumeProjectQueueAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"projects/{id}/queue/resume", new { }, cancellationToken);

    public Task<RawJson?> GetWorkItemBudgetUsageAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/budget/usage", cancellationToken);

    public Task<RawJson?> GetReplaysAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/replays", cancellationToken);

    public Task<RawJson?> GetFailureEventsAsync(string query = "", CancellationToken cancellationToken = default)
        => GetRaw($"workitems/failure-events{query}", cancellationToken);

    public Task<RawJson?> GetAggregateTimingsAsync(CancellationToken cancellationToken = default)
        => GetRaw("workitems/timings/aggregate", cancellationToken);

    public Task<RawJson?> GetAggregateAgentStreamsAsync(CancellationToken cancellationToken = default)
        => GetRaw("workitems/agent-streams/aggregate", cancellationToken);

    public Task<RawJson?> GetAgentStreamAnalysisAsync(string id, string fileName, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/agent-streams/{fileName}/analysis", cancellationToken);

    /// <summary>One auditor's raw report for an iteration. Text, not JSON — it is a report, not a record.</summary>
    public async Task<string> GetAuditReportRawAsync(string id, string target, int iteration, string auditor, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"workitems/{id}/audit-reports/{target}/{iteration}/{auditor}/raw", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    public Task PatchWorkItemAsync(string id, object changes, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"workitems/{id}")
        {
            Content = JsonContent.Create(changes, options: Json),
        }, cancellationToken);

    public Task PatchExternalIdsAsync(string id, IReadOnlyDictionary<string, string> externalIds, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"workitems/{id}/external-ids")
        {
            Content = JsonContent.Create(externalIds, options: Json),
        }, cancellationToken);

    public Task DeleteAttachmentAsync(string id, string attachmentId, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"workitems/{id}/attachments/{attachmentId}"), cancellationToken);

    public Task<RawJson?> GetAttachmentAsync(string id, string attachmentId, CancellationToken cancellationToken = default)
        => GetRaw($"workitems/{id}/attachments/{attachmentId}", cancellationToken);

    public Task<RawJson?> CreateReleaseAsync(object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync("releases", request, cancellationToken);

    public Task<RawJson?> GetReleaseAuditIterationsAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"releases/{id}/audit-iterations", cancellationToken);

    public async Task<int> GetSuggestionCountAsync(CancellationToken cancellationToken = default)
    {
        var raw = await GetRaw("suggestions/count", cancellationToken).ConfigureAwait(false);
        return raw?.Document.RootElement.TryGetProperty("count", out var count) == true ? count.GetInt32() : 0;
    }

    public Task<RawJson?> GetE2eRunAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"e2eruns/{id}", cancellationToken);

    public Task<RawJson?> GetE2eBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => GetRaw($"e2eruns/batches/{batchId}", cancellationToken);

    public Task<RawJson?> GetE2eBatchRunsAsync(string batchId, CancellationToken cancellationToken = default)
        => GetRaw($"e2eruns/batches/{batchId}/runs", cancellationToken);

    public Task<RawJson?> CreateE2eRunAsync(object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync("e2eruns", request, cancellationToken);

    public Task<RawJson?> CreateE2eRunsAsync(object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync("e2eruns/bulk", request, cancellationToken);

    public Task CancelE2eRunAsync(string id, CancellationToken cancellationToken = default)
        => PostJsonAsync($"e2eruns/{id}/cancel", new { }, cancellationToken);

    public Task<RawJson?> GetTestCaseAsync(string id, CancellationToken cancellationToken = default)
        => GetRaw($"testcases/{id}", cancellationToken);

    public Task<RawJson?> GetTestCasesForWorkItemAsync(string workItemId, CancellationToken cancellationToken = default)
        => GetRaw($"testcases/workitems/{workItemId}/testcases", cancellationToken);

    public Task<RawJson?> GetTestCaseRunsAsync(string testCaseId, CancellationToken cancellationToken = default)
        => GetRaw($"e2eruns/testcases/{testCaseId}/runs", cancellationToken);

    public Task<RawJson?> CreateTestCaseAsync(object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync("testcases", request, cancellationToken);

    public Task<RawJson?> CreateTestCasesAsync(object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync("testcases/bulk", request, cancellationToken);

    public Task UpdateTestCaseAsync(string id, object request, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Put, $"testcases/{id}")
        {
            Content = JsonContent.Create(request, options: Json),
        }, cancellationToken);

    public Task DeleteTestCaseAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"testcases/{id}"), cancellationToken);

    /// <summary>The plugin list as the orchestrator returns it, for the diagnostics pane to show verbatim
    /// alongside everything else it gathers.</summary>
    public Task<RawJson?> GetPluginsRawAsync(CancellationToken cancellationToken = default)
        => GetRaw("plugins", cancellationToken);

    public Task<RawJson?> GetBaselineImagesAsync(CancellationToken cancellationToken = default)
        => GetRaw("admin/baseline-images", cancellationToken);

    public Task MigrateBaselinesAsync(CancellationToken cancellationToken = default)
        => PostJsonAsync("baselines/migrate", new { }, cancellationToken);

    public Task<RawJson?> GetGitHubAppStatusAsync(CancellationToken cancellationToken = default)
        => GetRaw("github-app/status", cancellationToken);

    public Task<RawJson?> StartGitHubAppConnectAsync(CancellationToken cancellationToken = default)
        => GetRaw("github-app/start", cancellationToken);

    public Task<RawJson?> ConnectGitHubAppAsync(object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync("github-app/connect", request, cancellationToken);

    public Task<RawJson?> QueueTemplatesAsync(object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync("templates/queue", request, cancellationToken);

    public Task<RawJson?> CreateProjectReleaseAsync(string projectId, object request, CancellationToken cancellationToken = default)
        => PostForJsonAsync($"projects/{projectId}/release", request, cancellationToken);

    /// <summary>
    /// Follows every supervision session rather than one, for a view that watches the whole fleet.
    /// Sessions arrive on the same connection as <see cref="FollowAsync"/>'s output.
    /// </summary>
    public async Task FollowAllSupervisionAsync(CancellationToken cancellationToken = default)
    {
        var hub = await EnsureHubAsync(cancellationToken).ConfigureAwait(false);
        await hub.InvokeAsync("SubscribeAllSupervisionAsync", cancellationToken).ConfigureAwait(false);
    }

    public async Task FollowSupervisionSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var hub = await EnsureHubAsync(cancellationToken).ConfigureAwait(false);
        await hub.InvokeAsync("SubscribeSupervisionSessionAsync", sessionId, cancellationToken).ConfigureAwait(false);
    }

    // ---- plumbing ----

    /// <summary>POSTs and returns the body, for endpoints whose answer carries the thing they created.</summary>
    private async Task<RawJson?> PostForJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : new RawJson(JsonDocument.Parse(text));
    }


    private async Task<T?> Get<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetFromJsonAsync<T>(path, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A surface the orchestrator has switched off answers 4xx/5xx rather than an empty list; a
            // panel showing nothing is the honest rendering of that, and better than one showing a stack.
            return default;
        }
    }

    /// <summary>Fetches a body this plugin does not model. Null when the endpoint is unavailable, which on
    /// a real instance is common — several diagnostic surfaces answer 503 when their feature is off.</summary>
    private async Task<RawJson?> GetRaw(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : new RawJson(JsonDocument.Parse(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private Task PostJsonAsync(string path, object body, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        }, cancellationToken);

    /// <summary>The tail of an item's agent output, for the scrollback a live subscription cannot replay.</summary>
    public async Task<string> GetStdoutTailAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"workitems/{workItemId}/stdout-tail", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    public Task CancelAsync(string workItemId, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"workitems/{workItemId}"), cancellationToken);

    public Task RetryAsync(string workItemId, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"workitems/{workItemId}/retry"), cancellationToken);

    public Task PromoteAsync(string workItemId, CancellationToken cancellationToken = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"workitems/{workItemId}/promote"), cancellationToken);

    /// <summary>
    /// Pauses the queue. The reason is <b>required</b> by the orchestrator — it rejects an empty one, a
    /// control character, or anything over 500 chars with a 400 — because a paused queue with no recorded
    /// reason is the thing nobody can explain an hour later.
    /// </summary>
    public Task PauseQueueAsync(string reason, CancellationToken cancellationToken = default)
        => PostJsonAsync("queue/pause", new { reason }, cancellationToken);

    /// <summary>Resumes the queue. Its own endpoint, not a pause with a flag flipped.</summary>
    public Task ResumeQueueAsync(CancellationToken cancellationToken = default)
        => PostJsonAsync("queue/resume", new { }, cancellationToken);

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Follows one work item's agent output, connecting the hub on first use. Following a second item
    /// unsubscribes the first, because the panel shows one at a time and leaving stale groups joined would
    /// interleave two agents' output into it.
    /// </summary>
    public async Task FollowAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        var hub = await EnsureHubAsync(cancellationToken).ConfigureAwait(false);

        if (_following is { } previous && previous != workItemId)
        {
            try
            {
                await hub.InvokeAsync("UnsubscribeAsync", previous, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: a dropped connection has already forgotten its groups.
            }
        }

        _following = workItemId;
        await hub.InvokeAsync("SubscribeAsync", workItemId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HubConnection> EnsureHubAsync(CancellationToken cancellationToken)
    {
        if (_hub is { } existing && existing.State != HubConnectionState.Disconnected)
        {
            return existing;
        }

        if (_hub is null)
        {
            var hub = new HubConnectionBuilder()
                .WithUrl($"{_options.BaseUrl}/hubs/agent-stdout", o =>
                    o.AccessTokenProvider = () => Task.FromResult(_options.ApiKey))
                .WithAutomaticReconnect()
                .Build();

            hub.On<StdoutChunk>("stdoutChunk", chunk => StdoutReceived?.Invoke(chunk));
            hub.On<JsonElement>("streamComplete", payload =>
                StreamCompleted?.Invoke(
                    payload.TryGetProperty("workItemId", out var id) ? id.GetString() ?? string.Empty : string.Empty));

            // A reconnect rejoins nothing on its own — server-side groups die with the connection — so the
            // item being followed is re-subscribed explicitly.
            hub.Reconnected += async _ =>
            {
                if (_following is { } item)
                {
                    try
                    {
                        await hub.InvokeAsync("SubscribeAsync", item).ConfigureAwait(false);
                    }
                    catch
                    {
                        // the next Follow will put it right
                    }
                }
            };

            _hub = hub;
        }

        await _hub.StartAsync(cancellationToken).ConfigureAwait(false);
        return _hub;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is { } hub)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }

        _http.Dispose();
    }
}
