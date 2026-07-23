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

    /// <summary>The models an adapter can be told to use — its live-probed list when the CLI supports one, else
    /// its static fallback (see <c>.ideas/providers/05-model-and-engine-selection.md</c>). Empty for adapters
    /// that don't implement <c>IModelListingAdapter</c>, so a client shows no model picker for them.</summary>
    Task<IReadOnlyList<Abstractions.ModelInfo>> ListModels(string adapterId);

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

    // ---- launch profiles (see .ideas/providers/04-profiles.md) ----
    // Named, reusable bundles of new-session launch options, persisted host-side and driven over the wire so
    // any paired client can manage them. A profile carries no secret, so LaunchProfile crosses the wire whole.

    /// <summary>The host's saved launch profiles.</summary>
    Task<IReadOnlyList<LaunchProfile>> GetLaunchProfiles();

    /// <summary>Upserts a launch profile (assigning an id when blank) and returns the stored profile.</summary>
    Task<LaunchProfile> SaveLaunchProfile(LaunchProfile profile);

    /// <summary>Deletes a launch profile by id.</summary>
    Task DeleteLaunchProfile(string id);

    /// <summary>Opens a new session from a saved profile: materializes it into an <see cref="OpenSessionRequest"/>
    /// (using the request's directory override when the profile is directory-agnostic) and opens it.</summary>
    Task<SessionInfo> OpenSessionFromProfile(OpenSessionFromProfileRequest request);
    // ---- Direct (external / "watch") sessions — .ideas/sessions/02-direct-vs-synced-sessions.md ----

    /// <summary>Sessions the installed CLIs created on their own (outside Agnes) for a working directory,
    /// read from those CLIs' on-disk logs. Empty when no adapter can discover them.</summary>
    Task<IReadOnlyList<Abstractions.ExternalSessionInfo>> DiscoverExternalSessions(string workspaceDirectory);

    /// <summary>Opens a live, read-only Agnes session that watches (tails) an externally-created CLI session,
    /// returning its <see cref="SessionInfo"/> (with <see cref="SessionInfo.ReadOnly"/> set). Watch-only — it
    /// never sends to the underlying CLI.</summary>
    Task<SessionInfo> AttachExternalSession(string adapterId, string externalId);

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

    // ---- CLI-fallback terminal (platform/03) ----
    // The client-facing surface over the session's ICliFallback/PTY. Output rides the session event stream
    // as TerminalOutputEvents (replayed via snapshot/tail), so only open/write/resize live here; bytes stay
    // byte[] on the wire. Additive/trailing.

    /// <summary>Opens a CLI-fallback terminal in a session and returns its terminal id. Its output is
    /// appended to the session log as <see cref="Abstractions.TerminalOutputEvent"/>s (no new channel).</summary>
    Task<string> OpenTerminal(string sessionId, OpenTerminalRequest request);

    /// <summary>Writes raw input bytes (keystrokes/paste) to an open fallback terminal.</summary>
    Task WriteTerminal(string sessionId, string terminalId, byte[] data);

    /// <summary>Resizes an open fallback terminal to <paramref name="columns"/> × <paramref name="rows"/>.</summary>
    Task ResizeTerminal(string sessionId, string terminalId, int columns, int rows);

    /// <summary>Starts a provider CLI's interactive login through the same CLI-fallback terminal path as the
    /// in-session terminal (platform/03 reuse discipline), returning the opened terminal id.</summary>
    Task<string> BeginProviderLogin(string adapterId);

    // ---- pending queue & send policy (sessions/03) ----
    // One ordered queue per SESSION (not per client), owned host-side; its state rides the session event
    // log as PendingQueueEvent snapshots, so no bespoke queue-sync channel is needed. Additive/trailing.

    /// <summary>Sets the session's send policy — what a send does while a turn is active.</summary>
    Task SetSendPolicy(string sessionId, Abstractions.SendPolicy policy);

    /// <summary>Submits a message under the session's send policy (queued, sent now, or interrupt-and-send).</summary>
    Task EnqueuePendingMessage(string sessionId, IReadOnlyList<Abstractions.ContentBlock> content);

    /// <summary>Moves a queued message to a new position in the session's pending queue.</summary>
    Task ReorderPendingMessage(string sessionId, string messageId, int newIndex);

    /// <summary>Interrupts the current turn and sends the named queued message ahead of the rest.</summary>
    Task SendPendingNow(string sessionId, string messageId);

    /// <summary>Removes a queued message from the session's pending queue.</summary>
    Task RemovePendingMessage(string sessionId, string messageId);

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

    /// <summary>Registers the calling device's push token against a notification channel and sets its toggles,
    /// so the host can page it (per <c>.ideas/notifications/01-push-notifications.md</c>) when a watched session
    /// crosses a trigger. Keyed to the caller's device identity, so revoking pairing also stops pushes.</summary>
    Task RegisterPushChannel(RegisterPushRequest request);

    /// <summary>Tells the host the calling device is (or is no longer) actively foregrounding a session, so a
    /// push for that session is suppressed on THIS device only while it's being watched here.</summary>
    Task SetSessionViewing(string sessionId, bool viewing);

    /// <summary>Submit the user's answers to an outstanding structured question set.</summary>
    Task AnswerQuestion(QuestionAnswerRequest response);

    /// <summary>Answer an external attention request (extensibility/06) surfaced in the approvals inbox:
    /// records the answer and, if the caller supplied a callback URL, delivers it out of band.</summary>
    Task AnswerAttentionRequest(AttentionAnswerRequest response);

    /// <summary>Resolve an approval-gated action (notifications/02 tier 2) from the inbox: approving runs the
    /// parked action, rejecting turns it down without running it.</summary>
    Task ResolveGatedApproval(GatedApprovalResolution resolution);

    /// <summary>Git state of the session's working directory.</summary>
    Task<GitStatus> GetGitStatus(string sessionId);

    /// <summary>Stages all changes and commits them in the session's working directory.</summary>
    Task<GitCommitResult> GitCommit(string sessionId, string message);

    /// <summary>Stashes the working tree's uncommitted changes; null if there was nothing to stash.</summary>
    Task<GitStashInfo?> GitStash(string sessionId);

    /// <summary>Restores a previously created stash by its id (sha).</summary>
    Task<GitOperationResult> GitPopStash(string sessionId, string stashId);

    /// <summary>Switches branch, optionally carrying uncommitted changes across as a stash.</summary>
    Task<GitSwitchResult> GitSwitchBranch(string sessionId, string branch, bool carryStash);

    /// <summary>Fast-forward-only pull. A diverged remote is refused server-side with a typed error.</summary>
    Task<GitPullResult> GitPull(string sessionId);

    /// <summary>Pushes the current branch, publishing it upstream when requested.</summary>
    Task<GitOperationResult> GitPush(string sessionId, bool publishBranch);

    /// <summary>Open pull requests on the forge owning the session's git remote (empty if unrecognized).</summary>
    Task<IReadOnlyList<Abstractions.PullRequestInfo>> ListPullRequests(string sessionId);

    /// <summary>Fetches and checks out a pull request into the session's working directory.</summary>
    Task<GitOperationResult> CheckoutPullRequest(string sessionId, string pullRequestId);

    /// <summary>The session's changed files scoped to this turn, this session, or the whole repository.</summary>
    Task<IReadOnlyList<string>> GetChangedFiles(string sessionId, ChangedFileScope scope);

    /// <summary>Suggests a commit message by summarizing the staged diff via a one-shot agent run (never commits).</summary>
    Task<CommitMessageSuggestion> GenerateCommitMessage(string sessionId);

    /// <summary>Review comments left on a project's files (durable across the sessions run against it).</summary>
    Task<IReadOnlyList<Abstractions.ReviewComment>> ListReviewComments(string projectId);

    /// <summary>Adds a review comment anchored to a file + line, returning it with its assigned id.</summary>
    Task<Abstractions.ReviewComment> AddReviewComment(AddReviewCommentRequest request);

    /// <summary>Removes a review comment by id.</summary>
    Task RemoveReviewComment(string id);

    /// <summary>Materializes an uploaded attachment to a gitignored dir in the session's workspace and
    /// returns the workspace-relative path to reference in a prompt (never inline binary).</summary>
    Task<string> UploadAttachment(string sessionId, string fileName, byte[] data);

    // ---- file browser (see .ideas/git-and-files/03-attachments-and-file-browser.md) ----
    // Structured file ops over the session's working directory. Every relativePath is validated host-side
    // (the shared WorkspacePaths guard) so a `..`-escaping path is rejected before any disk access.

    /// <summary>Lists a directory in the session's workspace (empty path = the root), directories first.</summary>
    Task<IReadOnlyList<FileEntry>> ListDirectory(string sessionId, string relativePath);

    /// <summary>Reads a file for preview (decoded text, or bytes + mime for a recognised image).</summary>
    Task<FileContent> ReadFile(string sessionId, string relativePath);

    /// <summary>Writes UTF-8 text to a workspace file (the quick-edit-without-an-agent-turn case).</summary>
    Task WriteFile(string sessionId, string relativePath, string content);

    /// <summary>Creates a directory (and any missing parents) in the workspace.</summary>
    Task CreateDirectory(string sessionId, string relativePath);

    /// <summary>Renames/moves a file or directory within the workspace.</summary>
    Task RenameEntry(string sessionId, string fromRelativePath, string toRelativePath);

    /// <summary>Deletes a file or directory (recursively) from the workspace.</summary>
    Task DeleteEntry(string sessionId, string relativePath);

    /// <summary>Reads a workspace file's raw bytes for download to the client device.</summary>
    Task<byte[]> DownloadFile(string sessionId, string relativePath);

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

    /// <summary>Whether the calling client may attach the host diagnostic bundle to a report — true only when
    /// the host operator enabled the capability AND this caller is the host owner. Clients use it to decide
    /// whether to offer the (off-by-default) "attach host diagnostics" opt-in control.</summary>
    Task<bool> CanAttachDiagnostics();
    // ---- prompt library (see .ideas/extensibility/02-prompts-skills-library.md) ----
    // Host-persisted saved prompts + slash-token templates, driven over the wire so any paired client can
    // manage the library on the host. The abstractions records are simple and cross the wire directly.

    /// <summary>The host's saved prompts.</summary>
    Task<IReadOnlyList<Abstractions.LibraryPrompt>> GetPrompts();

    /// <summary>Upserts a saved prompt (assigning an id when blank) and returns the stored prompt.</summary>
    Task<Abstractions.LibraryPrompt> SavePrompt(Abstractions.LibraryPrompt prompt);

    /// <summary>Deletes a saved prompt by id.</summary>
    Task DeletePrompt(string id);

    /// <summary>The host's slash-token templates.</summary>
    Task<IReadOnlyList<Abstractions.PromptTemplate>> GetPromptTemplates();

    /// <summary>Upserts a template keyed by its slash token and returns the stored template.</summary>
    Task<Abstractions.PromptTemplate> SavePromptTemplate(Abstractions.PromptTemplate template);

    /// <summary>Deletes a template by slash token.</summary>
    Task DeletePromptTemplate(string token);

    // ---- skill bundles + external registries (see .ideas/extensibility/02-prompts-skills-library.md) ----
    // A skill is a SKILL.md + supporting files managed as one unit. The library owns managed copies (source of
    // truth); registries are explicit import sources exposed as a plugin point.

    /// <summary>The host's saved skill bundles.</summary>
    Task<IReadOnlyList<Abstractions.LibrarySkill>> GetSkills();

    /// <summary>Deletes a skill bundle by id (removing its managed files as a unit).</summary>
    Task DeleteSkill(string id);

    /// <summary>The ids of the registered external skill-registry sources.</summary>
    Task<IReadOnlyList<string>> GetSkillRegistries();

    /// <summary>The skills a registry source currently offers (before import).</summary>
    Task<IReadOnlyList<Abstractions.RegistrySkillEntry>> GetRegistrySkills(string registryId);

    /// <summary>Fetches a registry entry and imports it into the library, returning the stored skill.</summary>
    Task<Abstractions.LibrarySkill> InstallSkillFromRegistry(string registryId, string entryId);

    // ---- connected-service quota (see .ideas/providers/03-quota-monitoring.md) ----
    // Pull-only for MVP: a client asks for a profile's usage snapshot when it wants to paint a badge; the host
    // serves it from a per-profile cache behind a staleness window, so redrawing the badge doesn't hammer the
    // provider's usage API. (An OnQuotaChanged push could be added later; today it's a client pull.) The
    // QuotaSnapshot record carries no secret — only plan/meter numbers — so it crosses the wire whole.

    /// <summary>The current quota/usage snapshot for a connected-service profile, or null when it can't be
    /// reported — an unknown profile, or a provider that doesn't implement the optional quota capability.
    /// A distinguishable "unavailable" (null) rather than an error, so a badge degrades cleanly.</summary>
    Task<Abstractions.QuotaSnapshot?> GetQuotaSnapshot(string profileId);
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
