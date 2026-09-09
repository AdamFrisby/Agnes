using Agnes.Host.Hosting;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.AspNetCore.Http;

namespace Agnes.Host.Sharing;

/// <summary>What a caller wants to do with a session.</summary>
public enum SessionAccessKind
{
    /// <summary>Watch it: read the transcript, receive the tail — and see the screen.</summary>
    Subscribe,

    /// <summary>Drive it: send prompts, type into a terminal, move the guest's mouse.</summary>
    Prompt,

    /// <summary>Answer its permission prompts.</summary>
    Approve,

    /// <summary>Change who else can reach it.</summary>
    Manage,
}

/// <summary>
/// The one place the "may this caller do this to this session" question is answered, folding the share
/// checks and the session-isolation grants together exactly as the SignalR hub always has.
/// <para>
/// It exists because the display channel is a <b>second</b> front door. A WebSocket on the main listener is
/// not a hub method, so it cannot inherit the hub's checks by being one — and a security policy written twice
/// is a policy that will eventually be two policies. Both doors now call this, so a change to sharing reaches
/// the screen and the transcript in the same commit.
/// </para>
/// </summary>
// Not sealed, and its two entry points are virtual, purely so the display channel's end-to-end tests can
// stand the endpoint up over a real socket with a stubbed decision — the sharing stack has its own tests and
// re-deriving it here would test the fake. Production has exactly one implementation.
public class SessionAccessDecider
{
    private readonly SessionAccessAuthorizer _access;
    private readonly SessionManager _sessions;
    private readonly DeviceRegistry _devices;

    public SessionAccessDecider(SessionAccessAuthorizer access, SessionManager sessions, DeviceRegistry devices)
    {
        _access = access;
        _sessions = sessions;
        _devices = devices;
    }

    /// <summary>Resolves a device token to the identities the sharing layer matches shares against.</summary>
    public virtual SharingCaller CallerFor(string? token)
        => new(_devices.ResolveCallerId(token), _devices.ResolveGitHubLogin(token), _devices.IsOwner(_devices.ResolveCallerId(token)));

    /// <summary>The caller behind a request that carries its device token in the standard query parameter.</summary>
    public SharingCaller CallerFor(HttpRequest request)
        => CallerFor(request.Query[WireProtocol.TokenParameter].ToString());

    /// <summary>
    /// The access decision: the share/host-owner check, plus the session's own owner, plus (only when session
    /// isolation is enabled) the group grants. The ownership lookup is an in-memory catalogue read, so it is
    /// asked unconditionally — a caller reaching the session they started is not an isolation feature, it is
    /// the baseline, and making it conditional is what left non-Owner devices staring at an empty list.
    /// </summary>
    public virtual async Task<bool> DecideAsync(
        string sessionId, SessionAccessKind kind, SharingCaller caller, CancellationToken cancellationToken = default)
    {
        var (owner, group) = _sessions.GetOwnership(sessionId);
        return kind switch
        {
            SessionAccessKind.Subscribe => await _access.CanSubscribeAsync(sessionId, owner, group, caller, cancellationToken).ConfigureAwait(false),
            SessionAccessKind.Prompt => await _access.CanPromptAsync(sessionId, owner, group, caller, cancellationToken).ConfigureAwait(false),
            SessionAccessKind.Approve => await _access.CanApprovePermissionsAsync(sessionId, owner, group, caller, cancellationToken).ConfigureAwait(false),
            _ => await _access.CanManageAsync(sessionId, owner, group, caller, cancellationToken).ConfigureAwait(false),
        };
    }
}
