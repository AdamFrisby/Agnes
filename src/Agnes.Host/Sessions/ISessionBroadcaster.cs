using Agnes.Abstractions;

namespace Agnes.Host.Sessions;

/// <summary>Publishes a stored (sequenced) event to all clients subscribed to a session.</summary>
public interface ISessionBroadcaster
{
    Task PublishAsync(string sessionId, SessionEvent @event);
}
