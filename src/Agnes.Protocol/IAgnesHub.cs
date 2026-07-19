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

    Task<SessionInfo> OpenSession(OpenSessionRequest request);

    /// <summary>Join a session's broadcast group and get a snapshot from <paramref name="sinceSequence"/>.</summary>
    Task<SessionSnapshot> Subscribe(string sessionId, long sinceSequence);

    Task Unsubscribe(string sessionId);

    Task Prompt(PromptRequest request);

    /// <summary>Cancels the in-flight turn for a session (maps to ACP <c>session/cancel</c>).</summary>
    Task Cancel(string sessionId);

    /// <summary>Switches the session mode (maps to ACP <c>session/set_mode</c>).</summary>
    Task SetMode(string sessionId, string modeId);

    Task RespondPermission(PermissionResponseRequest response);

    /// <summary>Git state of the session's working directory.</summary>
    Task<GitStatus> GetGitStatus(string sessionId);

    /// <summary>Stages all changes and commits them in the session's working directory.</summary>
    Task<GitCommitResult> GitCommit(string sessionId, string message);

    /// <summary>Schedules a recurring background task; returns it with its assigned id.</summary>
    Task<ScheduledTask> ScheduleTask(ScheduleTaskRequest request);

    /// <summary>Currently scheduled background tasks.</summary>
    Task<IReadOnlyList<ScheduledTask>> ListScheduledTasks();

    /// <summary>Removes a scheduled task.</summary>
    Task RemoveScheduledTask(string taskId);

    /// <summary>Completed background runs (newest first).</summary>
    Task<IReadOnlyList<InboxRun>> GetInbox();

    /// <summary>Pause / resume / delete the session's sandbox (no-op if it runs on the host).</summary>
    Task PauseSandbox(string sessionId);
    Task ResumeSandbox(string sessionId);
    Task DeleteSandbox(string sessionId);

    /// <summary>Current sandbox status of the session, or null if it runs on the host.</summary>
    Task<SandboxStatus?> GetSandboxStatus(string sessionId);
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
}
