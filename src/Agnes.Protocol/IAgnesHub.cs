namespace Agnes.Protocol;

/// <summary>
/// Well-known transport constants. The default binding is SignalR, but the
/// contract itself (below) is transport-agnostic.
/// </summary>
public static class WireProtocol
{
    /// <summary>Wire protocol version negotiated between host and client.</summary>
    public const int Version = 1;

    /// <summary>Default SignalR hub path on the host.</summary>
    public const string HubPath = "/agnes";

    /// <summary>Query-string / header key carrying the device bearer token.</summary>
    public const string TokenParameter = "access_token";
}

/// <summary>
/// Methods a client invokes on the host. Implemented by the host's SignalR hub;
/// invoked by <c>Agnes.Client</c>. Method names on the wire match these names.
/// </summary>
public interface IAgnesServer
{
    Task<HostInfo> GetHostInfo();

    Task<IReadOnlyList<AgentInfo>> ListAgents();

    /// <summary>Forces a fresh (cache-bypassing) auth-status check for one adapter and returns its refreshed
    /// <see cref="AgentInfo"/>. The host also broadcasts <see cref="IAgnesClient.OnAgentsChanged"/> so every
    /// connected client's picker updates. See <c>.ideas/providers/06-provider-authentication-detection.md</c>.</summary>
    Task<AgentInfo> CheckAuthStatus(string adapterId);

    /// <summary>Which host-level plugin-point capabilities are actually populated on this host (see
    /// <see cref="HostCapability"/>), so a client can hide/degrade a feature up front instead of
    /// discovering its absence via a failed call.</summary>
    Task<IReadOnlyList<HostCapability>> GetCapabilities();

    /// <summary>The client advertises what it can do; the host stores it for this connection and returns a
    /// reconciled view so both parties can gate features on what's usable end to end (see
    /// <c>.ideas/00c-client-plugins-and-negotiation.md</c>).</summary>
    Task<NegotiatedCapabilities> Negotiate(ClientCapabilities client);

    Task<SessionInfo> OpenSession(OpenSessionRequest request);

    /// <summary>Compute a fork plan (proposed target folder + sandbox-copy capability) for a session.
    /// Returns null if the session is unknown.</summary>
    Task<ForkPlan?> ProposeFork(string sessionId);

    /// <summary>Fork a session: copy its working folder and open a new session there (optionally CoW-cloning
    /// the sandbox). See <see cref="ForkSessionRequest"/>.</summary>
    Task<SessionInfo> ForkSession(ForkSessionRequest request);

    /// <summary>Replay-forks a session at a log point (branch the conversation); returns the child plus an optional composer draft.</summary>
    Task<ForkAtResult> ForkSessionAt(ForkAtRequest request);

    /// <summary>Join a session's broadcast group and get a snapshot from <paramref name="sinceSequence"/>.</summary>
    Task<SessionSnapshot> Subscribe(string sessionId, long sinceSequence);

    Task Unsubscribe(string sessionId);

    Task Prompt(PromptRequest request);

    /// <summary>Full-text search over this host's session transcripts (see
    /// <c>.ideas/ops/02-memory-search.md</c>). Returns ranked hits with a highlighted snippet; an empty
    /// list on a host without a memory index configured.</summary>
    Task<IReadOnlyList<Abstractions.MemorySearchResult>> SearchMemory(string query, MemorySearchOptionsDto options);

    /// <summary>Cancels the in-flight turn for a session (maps to ACP <c>session/cancel</c>).</summary>
    Task Cancel(string sessionId);

    /// <summary>Restart the agent process for a session (relaunch + resume its conversation) — recovery for
    /// a crashed/hung agent, and the manual fallback after auto-restart has paused.</summary>
    Task RestartAgent(string sessionId);

    /// <summary>Switches the session mode (maps to ACP <c>session/set_mode</c>).</summary>
    Task SetMode(string sessionId, string modeId);

    Task RespondPermission(PermissionResponseRequest response);

    /// <summary>Submit the user's answers to an outstanding structured question set.</summary>
    Task AnswerQuestion(QuestionAnswerRequest response);

    /// <summary>Git state of the session's working directory.</summary>
    Task<GitStatus> GetGitStatus(string sessionId);

    /// <summary>Stages all changes and commits them in the session's working directory.</summary>
    Task<GitCommitResult> GitCommit(string sessionId, string message);

    /// <summary>Review comments left on a project's files (durable across the sessions run against it).</summary>
    Task<IReadOnlyList<Abstractions.ReviewComment>> ListReviewComments(string projectId);

    /// <summary>Adds a review comment anchored to a file + line, returning it with its assigned id.</summary>
    Task<Abstractions.ReviewComment> AddReviewComment(AddReviewCommentRequest request);

    /// <summary>Removes a review comment by id.</summary>
    Task RemoveReviewComment(string id);

    /// <summary>Materializes an uploaded attachment to a gitignored dir in the session's workspace and
    /// returns the workspace-relative path to reference in a prompt (never inline binary).</summary>
    Task<string> UploadAttachment(string sessionId, string fileName, byte[] data);

    /// <summary>Marks a session read up to <paramref name="sequence"/> (clears unread), synced to all
    /// the user's clients.</summary>
    Task MarkSessionRead(string sessionId, long sequence);

    /// <summary>Marks a session unread (sticky until the next mark-read), synced to all clients.</summary>
    Task MarkSessionUnread(string sessionId);

    /// <summary>Schedules a recurring background task; returns it with its assigned id.</summary>
    Task<ScheduledTask> ScheduleTask(ScheduleTaskRequest request);

    /// <summary>Currently scheduled background tasks.</summary>
    Task<IReadOnlyList<ScheduledTask>> ListScheduledTasks();

    /// <summary>Removes a scheduled task.</summary>
    Task RemoveScheduledTask(string taskId);

    /// <summary>Pauses a scheduled task: its schedule stops firing until resumed (persisted state).</summary>
    Task PauseScheduledTask(string taskId);

    /// <summary>Resumes a paused task on its existing schedule (no re-creation).</summary>
    Task ResumeScheduledTask(string taskId);

    /// <summary>Runs a scheduled task once immediately, out of band, without disturbing its regular schedule.</summary>
    Task RunScheduledTaskNow(string taskId);

    /// <summary>Completed background runs (newest first).</summary>
    Task<IReadOnlyList<InboxRun>> GetInbox();

    /// <summary>Open permission requests across every session the caller is authorized to see that still
    /// need a human, newest first — the cross-session approvals list (notifications/02 tier 1).</summary>
    Task<IReadOnlyList<OpenApproval>> GetOpenApprovals();

    /// <summary>Pause / resume / delete the session's sandbox (no-op if it runs on the host).</summary>
    Task PauseSandbox(string sessionId);
    Task ResumeSandbox(string sessionId);
    Task DeleteSandbox(string sessionId);

    /// <summary>Stop-on-close: end the agent and shut the sandbox VM down, but keep it for resume.</summary>
    Task StopSession(string sessionId);

    /// <summary>Current sandbox status of the session, or null if it runs on the host.</summary>
    Task<SandboxStatus?> GetSandboxStatus(string sessionId);

    // ---- plugin management (see .ideas/00-plugin-architecture.md) ----
    // Driven over the wire so a paired phone can install a plugin on a desktop host exactly as easily
    // as someone sitting at that host (AC12). All six mirror IPluginInstaller.

    /// <summary>Searches the configured NuGet source(s) for installable Agnes plugins.</summary>
    Task<IReadOnlyList<PluginSearchResultDto>> SearchPlugins(string query);

    /// <summary>Installs (downloads, verifies, loads) a plugin. Returns a typed outcome — a
    /// consent-required refusal is a normal result the client acts on, not an exception.</summary>
    Task<PluginInstallOutcome> InstallPlugin(InstallPluginRequest request);

    /// <summary>Updates an installed plugin to the latest version; same consent semantics as install.</summary>
    Task<PluginInstallOutcome> UpdatePlugin(string pluginId, IReadOnlyList<string> grantedCapabilities);

    /// <summary>Enables or disables an installed plugin (unloads/reloads it; no host restart).</summary>
    Task SetPluginEnabled(string pluginId, bool enabled);

    /// <summary>Applies the plugin's flat settings (from the Configure panel) and reloads it if enabled.</summary>
    Task ConfigurePlugin(string pluginId, IReadOnlyDictionary<string, string> settings);

    /// <summary>Uninstalls a plugin and removes its files.</summary>
    Task UninstallPlugin(string pluginId);

    /// <summary>Every installed plugin and its state.</summary>
    Task<IReadOnlyList<InstalledPluginDto>> ListInstalledPlugins();

    // ---- bug reports (see .ideas/ops/01-bug-reports-and-diagnostics.md) ----

    /// <summary>Submits a user-authored bug report to the host's configured sink. The typed result carries a
    /// created issue URL, a prefilled browser-fallback URL, and/or likely-duplicate issues to comment on.</summary>
    Task<Abstractions.BugReportResult> SubmitBugReport(BugReportDto report);
}

/// <summary>
/// Methods the host pushes to a client (SignalR strongly-typed client contract).
/// </summary>
public interface IAgnesClient
{
    /// <summary>A new appended event for a session the client is subscribed to.</summary>
    Task OnSessionEvent(string sessionId, Abstractions.SessionEvent @event);

    /// <summary>The set of available agents on the host changed.</summary>
    Task OnAgentsChanged(IReadOnlyList<AgentInfo> agents);

    /// <summary>A background run completed and landed in the inbox.</summary>
    Task OnInboxRun(InboxRun run);

    /// <summary>A session's read state changed (last-viewed sequence + a sticky "marked unread" flag), so
    /// unread indicators stay in sync across a user's devices.</summary>
    Task OnReadState(string sessionId, long readSequence, bool stickyUnread);
}
