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

    /// <summary>The set of available agents changed on the host.</summary>
    event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<HostInfo> GetHostInfoAsync();

    Task<IReadOnlyList<AgentInfo>> ListAgentsAsync();

    Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory);

    /// <summary>Subscribes to a session, returning a live view seeded from a snapshot.</summary>
    Task<SessionView> SubscribeAsync(string sessionId, long since = 0);

    Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content);

    Task RespondPermissionAsync(string sessionId, string requestId, string optionId);
}

/// <summary>Creates/looks up host connections. Swap the implementation to simulate a server.</summary>
public interface IAgnesConnector
{
    /// <summary>Connects to a host (or returns an existing connection for the same URL).</summary>
    Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default);

    /// <summary>Currently known hosts.</summary>
    IReadOnlyCollection<IAgnesHost> Hosts { get; }
}
