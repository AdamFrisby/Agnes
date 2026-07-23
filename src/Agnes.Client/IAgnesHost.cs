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

    Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true);

    /// <summary>Compute a fork plan (proposed target folder + sandbox-copy capability) for a session, or
    /// null if the session/host doesn't support forking. Default null for hosts without the feature.</summary>
    Task<ForkPlan?> ProposeForkAsync(string sessionId) => Task.FromResult<ForkPlan?>(null);

    /// <summary>Fork a session: copy its working folder to <paramref name="targetDirectory"/> and open a new
    /// session there, optionally CoW-cloning the sandbox.</summary>
    Task<SessionInfo> ForkSessionAsync(string sourceSessionId, string targetDirectory, bool copySandbox = true)
        => throw new NotSupportedException("This host does not support forking sessions.");

    /// <summary>Subscribes to a session, returning a live view seeded from a snapshot.</summary>
    Task<SessionView> SubscribeAsync(string sessionId, long since = 0);

    Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content);

    /// <summary>Cancels the in-flight turn for a session (Stop).</summary>
    Task CancelAsync(string sessionId);

    /// <summary>Restart the agent process for a session (relaunch + resume). Default no-op for hosts without
    /// the capability.</summary>
    Task RestartAgentAsync(string sessionId) => Task.CompletedTask;

    /// <summary>Switches the session's mode (Ask / Code / …).</summary>
    Task SetModeAsync(string sessionId, string modeId);

    Task RespondPermissionAsync(string sessionId, string requestId, string optionId);

    /// <summary>Submit the user's answers to an outstanding structured question set. Default no-op for hosts
    /// whose agents never ask questions.</summary>
    Task AnswerQuestionAsync(string sessionId, string requestId, IReadOnlyList<Agnes.Abstractions.QuestionAnswer> answers)
        => Task.CompletedTask;

    /// <summary>Git state of the session's working directory.</summary>
    Task<GitStatus> GetGitStatusAsync(string sessionId);

    /// <summary>Stages all changes and commits them.</summary>
    Task<GitCommitResult> GitCommitAsync(string sessionId, string message);

    /// <summary>Uploads an attachment's bytes; the host materializes it into the workspace and returns the
    /// workspace-relative path to reference in a prompt.</summary>
    Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data);

    /// <summary>Schedules a recurring background task.</summary>
    Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request);

    /// <summary>Lists scheduled background tasks.</summary>
    Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync();

    /// <summary>Removes a scheduled task.</summary>
    Task RemoveScheduledTaskAsync(string taskId);

    /// <summary>Completed background runs (newest first).</summary>
    Task<IReadOnlyList<InboxRun>> GetInboxAsync();

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
}

/// <summary>Creates/looks up host connections. Swap the implementation to simulate a server.</summary>
public interface IAgnesConnector
{
    /// <summary>Connects to a host (or returns an existing connection for the same URL).</summary>
    Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default);

    /// <summary>Currently known hosts.</summary>
    IReadOnlyCollection<IAgnesHost> Hosts { get; }
}
