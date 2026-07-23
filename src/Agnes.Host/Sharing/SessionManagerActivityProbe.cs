using Agnes.Host.Sessions;

namespace Agnes.Host.Sharing;

/// <summary>Production <see cref="ISessionActivityProbe"/>: a session is active when the
/// <see cref="SessionManager"/> holds it live (a running agent handle).</summary>
public sealed class SessionManagerActivityProbe : ISessionActivityProbe
{
    private readonly SessionManager _sessions;

    public SessionManagerActivityProbe(SessionManager sessions)
    {
        _sessions = sessions;
    }

    public bool IsActive(string sessionId) => _sessions.IsSessionLive(sessionId);
}
