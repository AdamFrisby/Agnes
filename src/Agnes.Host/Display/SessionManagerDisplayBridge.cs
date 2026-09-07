using Agnes.Abstractions;
using Agnes.Host.Sessions;
using Agnes.Sandbox;

namespace Agnes.Host.Display;

/// <summary>
/// The two threads that tie the display subsystem to the session layer, and the only ones: where a session's
/// display comes from, and where a control handover goes in the log. Kept as this one small adapter so the
/// broker, the arbiter and the endpoint know nothing about <see cref="SessionManager"/> — and so every one of
/// them is testable against a fake instead of a live host.
/// </summary>
public sealed class SessionManagerDisplayBridge : IDisplaySessionSource, IDisplayControlSink
{
    private readonly SessionManager _sessions;

    public SessionManagerDisplayBridge(SessionManager sessions) => _sessions = sessions;

    public IDisplaySource? DisplaySourceFor(string sessionId) => _sessions.DisplaySourceFor(sessionId);

    public bool IsTurnActive(string sessionId) => _sessions.IsTurnActive(sessionId);

    public Task AppendAsync(string sessionId, DisplayControlChangedEvent changed, CancellationToken cancellationToken = default)
        => _sessions.AppendDisplayControlAsync(sessionId, changed, cancellationToken);
}
