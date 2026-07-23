using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Host.Sessions;

/// <summary>Publishes a stored (sequenced) event to all clients subscribed to a session.</summary>
public interface ISessionBroadcaster
{
    Task PublishAsync(string sessionId, SessionEvent @event);

    /// <summary>Notifies every connected client that the agent list changed (e.g. a refreshed auth-status
    /// badge), so their pickers update. Default no-op for fixtures that don't fan out to clients.</summary>
    Task PublishAgentsChangedAsync(IReadOnlyList<AgentInfo> agents) => Task.CompletedTask;
}
