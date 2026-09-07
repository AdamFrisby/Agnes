using System.Collections.Concurrent;
using Agnes.Abstractions.Events;
using Agnes.Sandbox;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Display;

/// <summary>
/// Where a session's display comes from, and whether it still has a turn running — the two facts the broker
/// registry needs from the session layer, stated as an interface so the display subsystem never reaches into
/// <c>SessionManager</c> and can be tested against a fake.
/// </summary>
public interface IDisplaySessionSource
{
    /// <summary>The session's sandbox as a display source, or null when it is headless / not sandboxed / gone.</summary>
    IDisplaySource? DisplaySourceFor(string sessionId);

    /// <summary>Whether an agent turn is currently running on the session.</summary>
    bool IsTurnActive(string sessionId);
}

/// <summary>
/// The host's live display brokers, one per graphical session, created on demand.
/// <para>
/// Lazy and collapsing on purpose. Opening a capture connection costs a socket to the VM and a continuous
/// stream of pixels the host then has to buffer; a session nobody is watching, with no turn running, should
/// cost neither. So the first consumer creates the broker and the last one to leave takes it down — but only
/// while the agent is idle, because a turn in flight will be back for another screenshot in a second and
/// tearing the capture down between two tool calls would be pure churn.
/// </para>
/// </summary>
public sealed class DisplayBrokerRegistry : IAsyncDisposable
{
    private readonly IDisplaySessionSource _sessions;
    private readonly DisplayOptions _options;
    private readonly IEventBus _bus;
    private readonly IDisplayControlSink _sink;
    private readonly ILoggerFactory? _loggers;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DisplayBroker> _brokers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _createGate = new(1, 1);

    public DisplayBrokerRegistry(
        IDisplaySessionSource sessions,
        DisplayOptions options,
        IEventBus bus,
        IDisplayControlSink sink,
        ILoggerFactory? loggers = null,
        TimeProvider? time = null)
    {
        _sessions = sessions;
        _options = options;
        _bus = bus;
        _sink = sink;
        _loggers = loggers;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The broker for this session if one is already open, else null. Never creates.</summary>
    public DisplayBroker? Find(string sessionId) => _brokers.TryGetValue(sessionId, out var broker) ? broker : null;

    /// <summary>Whether the session has a display at all (its sandbox implements <see cref="IDisplaySource"/>).</summary>
    public bool HasDisplay(string sessionId) => _sessions.DisplaySourceFor(sessionId) is not null;

    /// <summary>
    /// The session's broker, opening the capture connection if this is the first consumer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session has no display.</exception>
    public async Task<DisplayBroker> GetOrCreateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_brokers.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_brokers.TryGetValue(sessionId, out existing))
            {
                return existing;
            }

            var source = _sessions.DisplaySourceFor(sessionId)
                ?? throw new InvalidOperationException("This session has no display.");

            var session = await source.OpenDisplayAsync(cancellationToken).ConfigureAwait(false);
            var broker = new DisplayBroker(
                sessionId, session, source.Display, _options, _bus, _sink,
                _loggers?.CreateLogger<DisplayBroker>(), _time);
            try
            {
                await broker.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await broker.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _brokers[sessionId] = broker;
            return broker;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Collapses every broker nobody is using: no subscribers, no turn running, and idle for at least
    /// <paramref name="grace"/>. Also expires any stale human hold on the brokers that survive — one sweep,
    /// both timers, so a host with no graphical sessions runs no display code at all.
    /// </summary>
    /// <returns>How many brokers were torn down.</returns>
    public async Task<int> SweepAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        var collapsed = 0;
        foreach (var (sessionId, broker) in _brokers.ToArray())
        {
            if (broker.SubscriberCount == 0
                && !_sessions.IsTurnActive(sessionId)
                && _time.GetUtcNow() - broker.LastActivityUtc >= grace)
            {
                if (_brokers.TryRemove(sessionId, out var removed))
                {
                    await removed.DisposeAsync().ConfigureAwait(false);
                    collapsed++;
                }

                continue;
            }

            await broker.ReleaseIdleControlAsync(cancellationToken).ConfigureAwait(false);
        }

        return collapsed;
    }

    /// <summary>Tears a session's broker down outright (the session closed, or its sandbox went away).</summary>
    public async Task CloseAsync(string sessionId)
    {
        if (_brokers.TryRemove(sessionId, out var broker))
        {
            await broker.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, broker) in _brokers.ToArray())
        {
            await broker.DisposeAsync().ConfigureAwait(false);
        }

        _brokers.Clear();
        _createGate.Dispose();
    }
}
