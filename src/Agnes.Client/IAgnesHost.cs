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
}

/// <summary>Creates/looks up host connections. Swap the implementation to simulate a server.</summary>
public interface IAgnesConnector
{
    /// <summary>Connects to a host (or returns an existing connection for the same URL).</summary>
    Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default);

    /// <summary>Currently known hosts.</summary>
    IReadOnlyCollection<IAgnesHost> Hosts { get; }
}
