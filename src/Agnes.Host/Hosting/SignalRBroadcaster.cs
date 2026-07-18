using Agnes.Abstractions;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace Agnes.Host.Hosting;

/// <summary>Broadcasts stored events to a session's SignalR group.</summary>
public sealed class SignalRBroadcaster(IHubContext<AgnesHub, IAgnesClient> hub) : ISessionBroadcaster
{
    public Task PublishAsync(string sessionId, SessionEvent @event)
        => hub.Clients.Group(sessionId).OnSessionEvent(sessionId, @event);
}
