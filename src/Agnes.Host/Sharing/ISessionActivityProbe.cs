namespace Agnes.Host.Sharing;

/// <summary>
/// Tells the sharing layer whether a session is currently <em>active</em> (known and live on this host) — the
/// precondition for granting permission-approval rights, since a collaborator can only answer prompts on a
/// running session. Kept as a tiny seam so the sharing domain does not depend on the whole
/// <c>SessionManager</c> and so tests can drive activity deterministically.
/// </summary>
public interface ISessionActivityProbe
{
    /// <summary>Whether <paramref name="sessionId"/> is a live, active session on this host right now.</summary>
    bool IsActive(string sessionId);
}
