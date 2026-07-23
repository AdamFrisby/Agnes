using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>Connection state of a host, independent of the transport.</summary>
public enum AgnesConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>
/// A client-side connection to one Agnes host. Implemented by the real SignalR
/// <see cref="HostConnection"/> and by simulated hosts, so the UI can be built and tested
/// against fake data without a server.
/// </summary>
public interface IAgnesHost : IAsyncDisposable
{
    /// <summary>The host's base URL (also used as the pool key).</summary>
    string HostUrl { get; }

    AgnesConnectionState State { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event Action<AgnesConnectionState>? StateChanged;

    // Token/cost usage is per-session, not per-host: it rides the session event stream as a
    // UsageReportedEvent and surfaces on SessionViewModel.Usage. See ClaudeCodeStreamMapper.

    /// <summary>The set of available agents changed on the host.</summary>
    event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<HostInfo> GetHostInfoAsync();

    Task<IReadOnlyList<AgentInfo>> ListAgentsAsync();

    /// <summary>The models an adapter can be told to use (live-probed or its static fallback). Default empty
    /// for hosts/fixtures that don't report models — a client then shows no model picker (see
    /// <c>.ideas/providers/05-model-and-engine-selection.md</c>).</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string adapterId)
        => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

    /// <summary>Forces a fresh (cache-bypassing) auth-status check for one adapter and returns its refreshed
    /// <see cref="AgentInfo"/>; the host also pushes <see cref="AgentsChanged"/> to every client. Default:
    /// unsupported (hosts/fixtures without auth detection).</summary>
    Task<AgentInfo> CheckAuthStatusAsync(string adapterId)
        => throw new NotSupportedException("This host does not support auth-status checks.");

    /// <summary>Which host-level plugin-point capabilities are populated on this host. Default empty
    /// for hosts/fixtures that don't report capabilities — a client should treat an id absent from the
    /// list the same as one explicitly reported unavailable.</summary>
    Task<IReadOnlyList<HostCapability>> GetCapabilitiesAsync() => Task.FromResult<IReadOnlyList<HostCapability>>([]);

    /// <summary>Advertises this client's capabilities to the host and returns the reconciled view. Defaulted
    /// so hosts/fixtures that predate negotiation reply as if the client shares no capabilities.</summary>
    Task<NegotiatedCapabilities> NegotiateAsync(ClientCapabilities client)
        => Task.FromResult(new NegotiatedCapabilities([]));

    Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null);

    // ---- launch profiles (see .ideas/providers/04-profiles.md) ----
    // Defaulted so hosts/fixtures that predate profiles reply empty (and reject writes) instead of failing.

    /// <summary>The host's saved launch profiles. Default empty for hosts without the feature.</summary>
    Task<IReadOnlyList<LaunchProfile>> GetLaunchProfilesAsync()
        => Task.FromResult<IReadOnlyList<LaunchProfile>>([]);

    /// <summary>Upserts a launch profile on the host, returning it with any assigned id.</summary>
    Task<LaunchProfile> SaveLaunchProfileAsync(LaunchProfile profile)
        => throw new NotSupportedException("This host does not support launch profiles.");

    /// <summary>Deletes a launch profile by id.</summary>
    Task DeleteLaunchProfileAsync(string id) => Task.CompletedTask;

    /// <summary>Opens a new session from a saved profile, optionally overriding its working directory (required
    /// when the profile is directory-agnostic).</summary>
    Task<SessionInfo> OpenSessionFromProfileAsync(string profileId, string? workingDirectoryOverride = null)
        => throw new NotSupportedException("This host does not support launch profiles.");
    /// <summary>Sessions the installed CLIs created on their own (outside Agnes) for a working directory, read
    /// from those CLIs' on-disk logs (see <c>.ideas/sessions/02-direct-vs-synced-sessions.md</c>). Default empty
    /// for hosts/fixtures without the feature.</summary>
    Task<IReadOnlyList<ExternalSessionInfo>> DiscoverExternalSessionsAsync(string workspaceDirectory)
        => Task.FromResult<IReadOnlyList<ExternalSessionInfo>>([]);

    /// <summary>Opens a live, read-only Agnes session that watches (tails) an externally-created CLI session.
    /// The returned <see cref="SessionInfo"/> has <see cref="SessionInfo.ReadOnly"/> set. Default: unsupported.</summary>
    Task<SessionInfo> AttachExternalSessionAsync(string adapterId, string externalId)
        => throw new NotSupportedException("This host does not support watching external sessions.");

    /// <summary>Compute a fork plan (proposed target folder + sandbox-copy capability) for a session, or
    /// null if the session/host doesn't support forking. Default null for hosts without the feature.</summary>
    Task<ForkPlan?> ProposeForkAsync(string sessionId) => Task.FromResult<ForkPlan?>(null);

    /// <summary>Fork a session: copy its working folder to <paramref name="targetDirectory"/> and open a new
    /// session there, optionally CoW-cloning the sandbox.</summary>
    Task<SessionInfo> ForkSessionAsync(string sourceSessionId, string targetDirectory, bool copySandbox = true)
        => throw new NotSupportedException("This host does not support forking sessions.");

    /// <summary>Replay-fork a session at a log point: branch the conversation, seeding the child with the
    /// parent's transcript up to <paramref name="atSequence"/>. Returns the child plus an editable draft
    /// when the fork point was a user message.</summary>
    Task<ForkAtResult> ForkSessionAtAsync(string sourceSessionId, string targetDirectory, long atSequence, bool copySandbox = true)
        => throw new NotSupportedException("This host does not support forking sessions.");

    /// <summary>Subscribes to a session, returning a live view seeded from a snapshot.</summary>
    Task<SessionView> SubscribeAsync(string sessionId, long since = 0);

    Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content);

    // ---- CLI-fallback terminal (platform/03) ----
    // Client-facing surface over the session's ICliFallback/PTY. Output rides the session event stream as
    // TerminalOutputEvents (rendered by TerminalPanelViewModel from the same SessionView), so only
    // open/write/resize are here. Defaulted so hosts/fixtures (SimulatedHost/RecordedHost/FakeHost) that
    // predate the terminal surface keep compiling.

    /// <summary>Opens a CLI-fallback terminal in a session and returns its terminal id; output rides the
    /// session event stream as <see cref="TerminalOutputEvent"/>s. Default: unsupported for hosts/fixtures
    /// without a terminal fallback.</summary>
    Task<string> OpenTerminalAsync(string sessionId, string? command = null, IReadOnlyList<string>? arguments = null, string? workingDirectory = null, int columns = 120, int rows = 30)
        => throw new NotSupportedException("This host does not support a CLI-fallback terminal.");

    /// <summary>Writes raw input bytes (keystrokes/paste) to an open fallback terminal. Default no-op.</summary>
    Task WriteTerminalAsync(string sessionId, string terminalId, byte[] data) => Task.CompletedTask;

    /// <summary>Resizes an open fallback terminal. Default no-op.</summary>
    Task ResizeTerminalAsync(string sessionId, string terminalId, int columns, int rows) => Task.CompletedTask;

    /// <summary>Starts a provider CLI's interactive login through the same CLI-fallback terminal path as the
    /// in-session terminal, returning the opened terminal id. Default: unsupported.</summary>
    Task<string> BeginProviderLoginAsync(string adapterId)
        => throw new NotSupportedException("This host does not support interactive provider login.");

    // ---- pending queue & send policy (sessions/03) ----
    // Defaulted so hosts/fixtures (SimulatedHost/RecordedHost/FakeHost) that predate the queue keep
    // compiling: a policy set is a no-op and an enqueue degrades to an immediate send.

    /// <summary>Sets the session's send policy (what a send does while a turn is active). Default no-op.</summary>
    Task SetSendPolicyAsync(string sessionId, SendPolicy policy) => Task.CompletedTask;

    /// <summary>Submits a message under the session's send policy (queued, sent now, or interrupt-and-send).
    /// Default: send immediately, so a host without the queue still delivers the message.</summary>
    Task EnqueuePendingMessageAsync(string sessionId, IReadOnlyList<ContentBlock> content)
        => PromptAsync(sessionId, content);

    /// <summary>Moves a queued message to a new position in the session's pending queue. Default no-op.</summary>
    Task ReorderPendingMessageAsync(string sessionId, string messageId, int newIndex) => Task.CompletedTask;

    /// <summary>Interrupts the current turn and sends the named queued message ahead of the rest. Default no-op.</summary>
    Task SendPendingNowAsync(string sessionId, string messageId) => Task.CompletedTask;

    /// <summary>Removes a queued message from the session's pending queue. Default no-op.</summary>
    Task RemovePendingMessageAsync(string sessionId, string messageId) => Task.CompletedTask;

    /// <summary>Full-text search over this host's session transcripts, ranked best-first with a highlighted
    /// snippet. Default empty for hosts/fixtures without a memory index (see
    /// <c>.ideas/ops/02-memory-search.md</c>).</summary>
    Task<IReadOnlyList<Agnes.Abstractions.MemorySearchResult>> SearchMemoryAsync(string query, Agnes.Abstractions.MemorySearchOptions options)
        => Task.FromResult<IReadOnlyList<Agnes.Abstractions.MemorySearchResult>>([]);

    /// <summary>Cancels the in-flight turn for a session (Stop).</summary>
    Task CancelAsync(string sessionId);

    /// <summary>Restart the agent process for a session (relaunch + resume). Default no-op for hosts without
    /// the capability.</summary>
    Task RestartAgentAsync(string sessionId) => Task.CompletedTask;

    /// <summary>Switches the session's mode (Ask / Code / …).</summary>
    Task SetModeAsync(string sessionId, string modeId);

    Task RespondPermissionAsync(string sessionId, string requestId, string optionId);

    /// <summary>Registers this device's push token against a host notification channel and sets its per-trigger
    /// toggles (notifications/01). Default no-op for hosts/fixtures without a push surface.</summary>
    Task RegisterPushChannelAsync(string channelId, string channelToken, PushNotificationPrefs prefs)
        => Task.CompletedTask;

    /// <summary>Signals the host that this device is (or is no longer) actively viewing a session, so a push
    /// for it is suppressed on this device only. Default no-op for hosts/fixtures without a push surface.</summary>
    Task SetSessionViewingAsync(string sessionId, bool viewing) => Task.CompletedTask;

    /// <summary>Answer an external attention request (extensibility/06) from the approvals inbox. Default no-op
    /// for hosts/fixtures that don't surface external attention requests.</summary>
    Task AnswerAttentionRequestAsync(string requestId, string answer) => Task.CompletedTask;

    /// <summary>Submit the user's answers to an outstanding structured question set. Default no-op for hosts
    /// whose agents never ask questions.</summary>
    Task AnswerQuestionAsync(string sessionId, string requestId, IReadOnlyList<Agnes.Abstractions.QuestionAnswer> answers)
        => Task.CompletedTask;

    /// <summary>Git state of the session's working directory.</summary>
    Task<GitStatus> GetGitStatusAsync(string sessionId);

    /// <summary>Stages all changes and commits them.</summary>
    Task<GitCommitResult> GitCommitAsync(string sessionId, string message);

    /// <summary>Stashes the working tree; null if nothing to stash. Default no-op for hosts without git.</summary>
    Task<GitStashInfo?> GitStashAsync(string sessionId)
        => Task.FromResult<GitStashInfo?>(null);

    /// <summary>Restores a previously created stash by its id. Default unsupported.</summary>
    Task<GitOperationResult> GitPopStashAsync(string sessionId, string stashId)
        => Task.FromResult(new GitOperationResult(false, "This host does not support git stash."));

    /// <summary>Switches branch, optionally carrying uncommitted changes across. Default unsupported.</summary>
    Task<GitSwitchResult> GitSwitchBranchAsync(string sessionId, string branch, bool carryStash)
        => Task.FromResult(new GitSwitchResult(false, false, null, "This host does not support branch switching."));

    /// <summary>Fast-forward-only pull (diverged remotes refused server-side). Default unsupported.</summary>
    Task<GitPullResult> GitPullAsync(string sessionId)
        => Task.FromResult(new GitPullResult(false, false, "This host does not support git pull."));

    /// <summary>Pushes the current branch, publishing it upstream when requested. Default unsupported.</summary>
    Task<GitOperationResult> GitPushAsync(string sessionId, bool publishBranch)
        => Task.FromResult(new GitOperationResult(false, "This host does not support git push."));

    /// <summary>Open pull requests on the forge owning the session's remote. Default empty.</summary>
    Task<IReadOnlyList<Agnes.Abstractions.PullRequestInfo>> ListPullRequestsAsync(string sessionId)
        => Task.FromResult<IReadOnlyList<Agnes.Abstractions.PullRequestInfo>>([]);

    /// <summary>Fetches and checks out a pull request into the session's working directory. Default unsupported.</summary>
    Task<GitOperationResult> CheckoutPullRequestAsync(string sessionId, string pullRequestId)
        => Task.FromResult(new GitOperationResult(false, "This host does not support pull-request checkout."));

    /// <summary>The session's changed files scoped to this turn, this session, or the whole repository.
    /// Default: empty (hosts without git report no scoped changes).</summary>
    Task<IReadOnlyList<string>> GetChangedFilesAsync(string sessionId, ChangedFileScope scope)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Suggests a commit message by summarizing the staged diff via a one-shot agent run (never commits).
    /// Default: no suggestion.</summary>
    Task<CommitMessageSuggestion> GenerateCommitMessageAsync(string sessionId)
        => Task.FromResult(new CommitMessageSuggestion(false, string.Empty));

    /// <summary>Review comments left on a project's files (durable across sessions). Default empty for hosts
    /// that don't store review comments.</summary>
    Task<IReadOnlyList<Agnes.Abstractions.ReviewComment>> ListReviewCommentsAsync(string projectId)
        => Task.FromResult<IReadOnlyList<Agnes.Abstractions.ReviewComment>>([]);

    /// <summary>Adds a review comment anchored to a file + line, returning it with its assigned id.</summary>
    Task<Agnes.Abstractions.ReviewComment> AddReviewCommentAsync(AddReviewCommentRequest request)
        => throw new NotSupportedException("This host does not support review comments.");

    /// <summary>Removes a review comment by id.</summary>
    Task RemoveReviewCommentAsync(string id) => Task.CompletedTask;

    /// <summary>Uploads an attachment's bytes; the host materializes it into the workspace and returns the
    /// workspace-relative path to reference in a prompt.</summary>
    Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data);

    // ---- file browser (see .ideas/git-and-files/03-attachments-and-file-browser.md) ----
    // Structured file ops over the session's working directory. Defaulted so hosts/fixtures that predate the
    // file browser stay compilable: reads default empty/unavailable, mutations no-op.

    /// <summary>Lists a directory in the session's workspace (empty path = the root), directories first.
    /// Default empty for hosts without a file browser.</summary>
    Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(string sessionId, string relativePath)
        => Task.FromResult<IReadOnlyList<FileEntry>>([]);

    /// <summary>Reads a file in the session's workspace for preview.</summary>
    Task<FileContent> ReadFileAsync(string sessionId, string relativePath)
        => throw new NotSupportedException("This host does not support the file browser.");

    /// <summary>Writes UTF-8 text to a workspace file (quick edit without an agent turn).</summary>
    Task WriteFileAsync(string sessionId, string relativePath, string content) => Task.CompletedTask;

    /// <summary>Creates a directory (and any missing parents) in the workspace.</summary>
    Task CreateDirectoryAsync(string sessionId, string relativePath) => Task.CompletedTask;

    /// <summary>Renames/moves a file or directory within the workspace.</summary>
    Task RenameEntryAsync(string sessionId, string fromRelativePath, string toRelativePath) => Task.CompletedTask;

    /// <summary>Deletes a file or directory (recursively) from the workspace.</summary>
    Task DeleteEntryAsync(string sessionId, string relativePath) => Task.CompletedTask;

    /// <summary>Reads a workspace file's raw bytes for download. Default empty for hosts without a browser.</summary>
    Task<byte[]> DownloadFileAsync(string sessionId, string relativePath) => Task.FromResult<byte[]>([]);

    /// <summary>Schedules a recurring background task.</summary>
    Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request);

    /// <summary>Lists scheduled background tasks.</summary>
    Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync();

    /// <summary>Removes a scheduled task.</summary>
    Task RemoveScheduledTaskAsync(string taskId);

    /// <summary>Pauses a scheduled task until it is resumed. Hosts that don't support automations no-op.</summary>
    Task PauseScheduledTaskAsync(string taskId) => Task.CompletedTask;

    /// <summary>Resumes a paused task on its existing schedule.</summary>
    Task ResumeScheduledTaskAsync(string taskId) => Task.CompletedTask;

    /// <summary>Runs a scheduled task once immediately, out of band, leaving its schedule intact.</summary>
    Task RunScheduledTaskNowAsync(string taskId) => Task.CompletedTask;

    /// <summary>Completed background runs (newest first).</summary>
    Task<IReadOnlyList<InboxRun>> GetInboxAsync();

    /// <summary>Open permission requests across every session on this host that still need a human, newest
    /// first — the cross-session approvals list (notifications/02 tier 1). Default empty for hosts/fixtures
    /// that don't aggregate approvals.</summary>
    Task<IReadOnlyList<OpenApproval>> GetOpenApprovalsAsync()
        => Task.FromResult<IReadOnlyList<OpenApproval>>([]);

    /// <summary>Raised when a background run lands in the inbox.</summary>
    event Action<InboxRun>? InboxRunReceived;

    /// <summary>A session's read state changed on the host (sessionId, last-viewed sequence, sticky-unread).</summary>
    event Action<string, long, bool>? ReadStateChanged;

    /// <summary>Marks a session read up to a sequence (clears unread), synced across the user's clients.</summary>
    Task MarkSessionReadAsync(string sessionId, long sequence);

    /// <summary>Marks a session unread (sticky until the next mark-read).</summary>
    Task MarkSessionUnreadAsync(string sessionId);

    /// <summary>Pauses the session's sandbox (no-op if it runs on the host).</summary>
    Task PauseSandboxAsync(string sessionId);

    /// <summary>Resumes the session's paused sandbox.</summary>
    Task ResumeSandboxAsync(string sessionId);

    /// <summary>Deletes the session's sandbox.</summary>
    Task DeleteSandboxAsync(string sessionId);

    /// <summary>Explicit stop-on-close: end the agent and shut the sandbox VM down (kept for resume).</summary>
    Task StopSessionAsync(string sessionId);

    /// <summary>Current sandbox status of the session, or null if it runs on the host.</summary>
    Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId);

    // ---- plugin management (see .ideas/00-plugin-architecture.md) ----
    // Defaulted so hosts/fixtures that predate plugin management don't have to implement them.

    /// <summary>Searches the host's configured NuGet source(s) for installable plugins.</summary>
    Task<IReadOnlyList<PluginSearchResultDto>> SearchPluginsAsync(string query)
        => Task.FromResult<IReadOnlyList<PluginSearchResultDto>>([]);

    /// <summary>Installs a plugin on the host; returns a typed outcome (a consent-required refusal is a
    /// normal result to act on, not an exception).</summary>
    Task<PluginInstallOutcome> InstallPluginAsync(InstallPluginRequest request)
        => Task.FromResult(new PluginInstallOutcome(false, null, false, [], "Plugin management is not available on this host."));

    /// <summary>Updates an installed plugin to its latest version; same consent semantics as install.</summary>
    Task<PluginInstallOutcome> UpdatePluginAsync(string pluginId, IReadOnlyList<string> grantedCapabilities)
        => Task.FromResult(new PluginInstallOutcome(false, null, false, [], "Plugin management is not available on this host."));

    /// <summary>Enables or disables an installed plugin.</summary>
    Task SetPluginEnabledAsync(string pluginId, bool enabled) => Task.CompletedTask;

    /// <summary>Applies the plugin's flat settings and reloads it if enabled.</summary>
    Task ConfigurePluginAsync(string pluginId, IReadOnlyDictionary<string, string> settings) => Task.CompletedTask;

    /// <summary>Uninstalls a plugin and removes its files.</summary>
    Task UninstallPluginAsync(string pluginId) => Task.CompletedTask;

    /// <summary>Every plugin installed on the host and its state.</summary>
    Task<IReadOnlyList<InstalledPluginDto>> ListInstalledPluginsAsync()
        => Task.FromResult<IReadOnlyList<InstalledPluginDto>>([]);

    /// <summary>Submits a user-authored bug report to the host's configured sink. Default for hosts/fixtures
    /// without bug reporting: report it as unavailable so the client falls back to the public browser flow.</summary>
    Task<Agnes.Abstractions.BugReportResult> SubmitBugReportAsync(BugReportDto report)
        => Task.FromResult(new Agnes.Abstractions.BugReportResult(false, null, "Bug reporting is not available on this host."));

    /// <summary>Whether this client may attach the host diagnostic bundle to a report (owner-only + opt-in
    /// capability enabled). Default false, so the sensitive control stays hidden on hosts/fixtures that don't
    /// offer it.</summary>
    Task<bool> CanAttachDiagnosticsAsync() => Task.FromResult(false);
    // ---- prompt library (see .ideas/extensibility/02-prompts-skills-library.md) ----
    // Defaulted so hosts/fixtures that predate the library reply empty (and reject writes) instead of failing.

    /// <summary>The host's saved prompts. Default empty for hosts without a library.</summary>
    Task<IReadOnlyList<LibraryPrompt>> GetPromptsAsync()
        => Task.FromResult<IReadOnlyList<LibraryPrompt>>([]);

    /// <summary>Upserts a saved prompt on the host, returning it with any assigned id.</summary>
    Task<LibraryPrompt> SavePromptAsync(LibraryPrompt prompt)
        => throw new NotSupportedException("This host does not support a prompt library.");

    /// <summary>Deletes a saved prompt by id.</summary>
    Task DeletePromptAsync(string id) => Task.CompletedTask;

    /// <summary>The host's slash-token templates. Default empty for hosts without a library.</summary>
    Task<IReadOnlyList<PromptTemplate>> GetPromptTemplatesAsync()
        => Task.FromResult<IReadOnlyList<PromptTemplate>>([]);

    /// <summary>Upserts a template on the host, returning the stored template.</summary>
    Task<PromptTemplate> SavePromptTemplateAsync(PromptTemplate template)
        => throw new NotSupportedException("This host does not support a prompt library.");

    /// <summary>Deletes a template by slash token.</summary>
    Task DeletePromptTemplateAsync(string token) => Task.CompletedTask;

    // ---- skill bundles + external registries (see .ideas/extensibility/02-prompts-skills-library.md) ----
    // Defaulted so hosts/fixtures that predate skills reply empty (and reject writes) instead of failing.

    /// <summary>The host's saved skill bundles. Default empty for hosts without a skill library.</summary>
    Task<IReadOnlyList<LibrarySkill>> GetSkillsAsync()
        => Task.FromResult<IReadOnlyList<LibrarySkill>>([]);

    /// <summary>Deletes a skill bundle by id.</summary>
    Task DeleteSkillAsync(string id) => Task.CompletedTask;

    /// <summary>The ids of the host's external skill-registry sources. Default empty.</summary>
    Task<IReadOnlyList<string>> GetSkillRegistriesAsync()
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>The skills a registry source currently offers. Default empty.</summary>
    Task<IReadOnlyList<RegistrySkillEntry>> GetRegistrySkillsAsync(string registryId)
        => Task.FromResult<IReadOnlyList<RegistrySkillEntry>>([]);

    /// <summary>Fetches a registry entry and imports it into the host's library, returning the stored skill.</summary>
    Task<LibrarySkill> InstallSkillFromRegistryAsync(string registryId, string entryId)
        => throw new NotSupportedException("This host does not support skill registries.");

    // ---- connected-service quota (see .ideas/providers/03-quota-monitoring.md) ----
    // Defaulted null so hosts/fixtures that predate quota reporting reply "unavailable" instead of failing.

    /// <summary>The current quota/usage snapshot for a connected-service profile, or null when it can't be
    /// reported. Default null for hosts without quota reporting — a client renders that as "unavailable".</summary>
    Task<QuotaSnapshot?> GetQuotaSnapshotAsync(string profileId)
        => Task.FromResult<QuotaSnapshot?>(null);
}

/// <summary>Creates/looks up host connections. Swap the implementation to simulate a server.</summary>
public interface IAgnesConnector
{
    /// <summary>Connects to a host (or returns an existing connection for the same URL).</summary>
    Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default);

    /// <summary>Currently known hosts.</summary>
    IReadOnlyCollection<IAgnesHost> Hosts { get; }
}
