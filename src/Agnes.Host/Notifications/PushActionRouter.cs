using Agnes.Host.Hosting;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Notifications;

/// <summary>The outcome of a tapped push interactive action.</summary>
public enum PushActionOutcome
{
    /// <summary>The token+session checked out and the decision was routed through the real permission path.</summary>
    Approved,

    /// <summary>The action was NOT auto-executed — the device must open the app to a confirmation/pairing flow.
    /// Returned whenever the acting device does not hold a currently-valid token for this host, or the session
    /// is unrecognized. Never a silent approval.</summary>
    OpenAppRequired,
}

/// <summary>
/// The untrusted-host safety guard for a push notification's interactive <c>Allow</c>/<c>Deny</c> action. A
/// push payload is not, by itself, an authenticated channel — it can be delayed, replayed, or crafted to look
/// like a session the user recognizes — so a tapped action must only auto-execute
/// <see cref="SessionManager.RespondPermissionAsync"/> (the SAME path a paired client uses) when the acting
/// device already holds a currently-valid bearer token for this host AND names a session this host knows.
/// <para>
/// Otherwise — a revoked pairing, an unrecognized device, a stale/forged token, or an unknown session — the
/// guard REJECTS the auto-action and returns <see cref="PushActionOutcome.OpenAppRequired"/>, turning
/// "notification spoofing" from a security bug into, at worst, a mildly annoying extra tap. It never silently
/// approves on an unrecognized/stale token.
/// </para>
/// </summary>
public sealed class PushActionRouter
{
    private readonly DeviceRegistry _devices;
    private readonly SessionManager _sessions;
    private readonly ILogger<PushActionRouter>? _logger;

    public PushActionRouter(DeviceRegistry devices, SessionManager sessions, ILogger<PushActionRouter>? logger = null)
    {
        _devices = devices;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to answer a permission request from a tapped push action. <paramref name="bearerToken"/> is the
    /// token the acting device currently holds; it must resolve (in constant time) to <paramref name="deviceId"/>
    /// for the auto-action to proceed. Returns <see cref="PushActionOutcome.OpenAppRequired"/> — without
    /// approving anything — if the token is missing/invalid/stale, resolves to a different device, or the
    /// session is unknown.
    /// </summary>
    public async Task<PushActionOutcome> RespondToPermissionAsync(
        string deviceId,
        string? bearerToken,
        string sessionId,
        string requestId,
        string optionId,
        CancellationToken cancellationToken = default)
    {
        var caller = _devices.ResolveCallerId(bearerToken);
        if (caller is null || !string.Equals(caller, deviceId, StringComparison.Ordinal))
        {
            // No valid token, or a token belonging to a different device than the one the action names.
            _logger?.LogWarning(
                "Rejected push action for device {Device} on session {Session}: no valid matching token — open-app fallback",
                deviceId, sessionId);
            return PushActionOutcome.OpenAppRequired;
        }

        if (!_sessions.KnowsSession(sessionId))
        {
            _logger?.LogWarning(
                "Rejected push action for device {Device}: unknown session {Session} — open-app fallback", deviceId, sessionId);
            return PushActionOutcome.OpenAppRequired;
        }

        // Identical path to a paired-device approval: dispatches the Before* spine hook and forwards to the
        // live session. This is the only sanctioned way a push action changes an outcome.
        await _sessions.RespondPermissionAsync(sessionId, requestId, optionId).ConfigureAwait(false);
        _logger?.LogInformation(
            "Device {Device} answered {Request} on session {Session} via push action with {Option}",
            deviceId, requestId, sessionId, optionId);
        return PushActionOutcome.Approved;
    }
}
