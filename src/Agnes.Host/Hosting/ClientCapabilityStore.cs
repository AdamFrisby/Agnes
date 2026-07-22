using System.Collections.Concurrent;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// Holds each connected client's advertised <see cref="ClientCapabilities"/> for the life of its
/// connection, so the host can reason about what a given client can actually do (e.g. skip pushing a
/// voice prompt to a client with no audio). Populated by <c>Negotiate</c>, cleared on disconnect.
/// Singleton: the SignalR hub is transient per call, so this state can't live on the hub instance.
/// </summary>
public sealed class ClientCapabilityStore
{
    private readonly ConcurrentDictionary<string, ClientCapabilities> _byConnection = new();

    public void Set(string connectionId, ClientCapabilities capabilities) => _byConnection[connectionId] = capabilities;

    public ClientCapabilities? Get(string connectionId) => _byConnection.GetValueOrDefault(connectionId);

    public void Remove(string connectionId) => _byConnection.TryRemove(connectionId, out _);
}
