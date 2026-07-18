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
}
