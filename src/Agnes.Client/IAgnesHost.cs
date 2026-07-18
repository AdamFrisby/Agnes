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

    /// <summary>Free-form status the host reports (e.g. usage/quota), or null if unknown.</summary>
    string? UsageSummary { get; }

    /// <summary>Structured usage (context-window / quota), or null if the host doesn't report it.</summary>
    UsageInfo? Usage { get; }

    /// <summary>Raised when <see cref="UsageSummary"/> / <see cref="Usage"/> changes.</summary>
    event Action<string?>? UsageChanged;

    /// <summary>The set of available agents changed on the host.</summary>
    event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<HostInfo> GetHostInfoAsync();

    Task<IReadOnlyList<AgentInfo>> ListAgentsAsync();

    Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false);

    /// <summary>Subscribes to a session, returning a live view seeded from a snapshot.</summary>
    Task<SessionView> SubscribeAsync(string sessionId, long since = 0);

    Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content);

    /// <summary>Cancels the in-flight turn for a session (Stop).</summary>
    Task CancelAsync(string sessionId);

    /// <summary>Switches the session's mode (Ask / Code / …).</summary>
    Task SetModeAsync(string sessionId, string modeId);

    Task RespondPermissionAsync(string sessionId, string requestId, string optionId);

    /// <summary>Git state of the session's working directory.</summary>
    Task<GitStatus> GetGitStatusAsync(string sessionId);

    /// <summary>Stages all changes and commits them.</summary>
    Task<GitCommitResult> GitCommitAsync(string sessionId, string message);
}

/// <summary>Creates/looks up host connections. Swap the implementation to simulate a server.</summary>
public interface IAgnesConnector
{
    /// <summary>Connects to a host (or returns an existing connection for the same URL).</summary>
    Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default);

    /// <summary>Currently known hosts.</summary>
    IReadOnlyCollection<IAgnesHost> Hosts { get; }
}
