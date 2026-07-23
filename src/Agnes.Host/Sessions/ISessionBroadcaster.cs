using Agnes.Abstractions;

namespace Agnes.Host.Sessions;

/// <summary>Publishes a stored (sequenced) event to all clients subscribed to a session.</summary>
public interface ISessionBroadcaster
{
    Task PublishAsync(string sessionId, SessionEvent @event);

    /// <summary>Pushes a session's read state (last-viewed sequence + sticky-unread flag) to its subscribed
    /// clients, so read/unread stays in sync across a user's devices. Defaulted to a no-op so lightweight
    /// broadcasters (tests) needn't implement it.</summary>
    Task PublishReadStateAsync(string sessionId, long readSequence, bool stickyUnread) => Task.CompletedTask;
}
