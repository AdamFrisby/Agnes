using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Wrap;

/// <summary>
/// A minimal in-process host for the wrapper: a <see cref="SessionManager"/> backed by an in-memory event
/// store, with the wrapped CLI registered as its one agent. This is the "register the wrapped session with a
/// host" half of sessions/07 in its simplest form — enough to make the terminal a real, catalogued,
/// snapshot/tail-able, handoff-capable Agnes session. A production wrapper would instead register over the
/// local daemon's loopback control endpoint (so paired remote clients see it live); that transport is the
/// interactive/remote outer loop, deliberately out of this MVP's testable core.
/// </summary>
public sealed class LocalWrapperHost : IAsyncDisposable
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private readonly WrappedCliAdapter _adapter;

    private LocalWrapperHost(SessionManager manager, WrappedCliAdapter adapter, SessionInfo session)
    {
        Manager = manager;
        _adapter = adapter;
        Session = session;
    }

    /// <summary>The host's session manager (the same type a full host runs).</summary>
    public SessionManager Manager { get; }

    /// <summary>The catalogued session for the wrapped CLI.</summary>
    public SessionInfo Session { get; }

    /// <summary>The live wrapped-terminal session, for teeing local I/O.</summary>
    public WrappedCliSession Terminal =>
        _adapter.LastSession ?? throw new InvalidOperationException("The wrapped session has not started.");

    /// <summary>Spawns <paramref name="command"/> in a PTY and opens it as an Agnes session on a fresh
    /// in-process host, returning the wired-up host.</summary>
    public static async Task<LocalWrapperHost> StartAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IPtySpawnerFactory? spawnerFactory = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        var adapter = new WrappedCliAdapter(command, arguments, spawnerFactory?.Create());
        var manager = new SessionManager(
            new PluginRegistry<IAgentAdapter>([adapter], a => a.Descriptor.Id),
            new InMemoryEventStore(),
            new NullBroadcaster(),
            loggerFactory ?? NullLoggerFactory.Instance);

        var session = await manager.OpenSessionAsync(
            WrappedCliAdapter.AdapterId, workingDirectory, useSandbox: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new LocalWrapperHost(manager, adapter, session);
    }

    public ValueTask DisposeAsync() => Manager.DisposeAsync();
}

/// <summary>Creates the PTY spawner the wrapper uses. Injected so a non-terminal caller can substitute one;
/// the default is the real <see cref="Pty.PortaPtySpawner"/>.</summary>
public interface IPtySpawnerFactory
{
    Pty.IPtySpawner Create();
}
