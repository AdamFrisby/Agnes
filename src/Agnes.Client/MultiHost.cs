namespace Agnes.Client;

/// <summary>
/// A single session tagged with the host it lives on — one row of the client's cross-host session aggregate.
/// Because a session id is only unique <em>within</em> a host, the <see cref="HostId"/> is what disambiguates
/// a same-named session reached through two different servers (multi-server support, <c>connectivity/02</c>).
/// </summary>
public sealed record HostSessionRef(string HostId, string HostUrl, ClientTransportKind Transport, SessionView Session)
{
    /// <summary>The session's id (unique within <see cref="HostId"/>).</summary>
    public string SessionId => Session.SessionId;
}

/// <summary>
/// A point-in-time snapshot of one pooled host: its identity, the transport reaching it, its live connection
/// state, and how many sessions the client currently holds on it. The host-list surface across a mix of
/// transports.
/// </summary>
public sealed record HostStatus(
    string HostId,
    string HostUrl,
    ClientTransportKind Transport,
    AgnesConnectionState State,
    int SessionCount);
