namespace Agnes.Abstractions.Events;

/// <summary>Marker for anything that flows through the <see cref="IEventBus"/>.</summary>
public interface IAgnesEvent;

/// <summary>
/// An action event that interceptors can veto or mutate before it commits (see
/// <c>.ideas/00d-event-spine-and-ui-extensibility.md</c>). Payload mutation is done by making a concrete
/// event's fields settable; the caller reads them back after dispatch.
/// </summary>
public abstract class CancelableEvent : IAgnesEvent
{
    public bool IsCanceled { get; private set; }
    public string? CancelReason { get; private set; }

    /// <summary>Vetoes the action. The first reason wins (a later cancel doesn't overwrite it).</summary>
    public void Cancel(string? reason = null)
    {
        IsCanceled = true;
        CancelReason ??= reason;
    }
}

/// <summary>Runs before an action commits; may mutate the event or cancel it. Lower <see cref="Order"/>
/// runs first; interceptors with equal <see cref="Order"/> run in registration order (a stable, documented
/// tiebreak — never load-order-dependent).
/// <para>
/// Order convention (so independently-authored plugins compose predictably): negative bands are for
/// system/security policy that must run first (e.g. an allow-list vetoing before anything else),
/// <c>0</c> is the default for ordinary plugins, and positive bands are for late/decorative handlers that
/// want to see the final mutated payload. Keep to these bands rather than inventing arbitrary numbers.
/// </para></summary>
public interface IEventInterceptor<in TEvent> where TEvent : IAgnesEvent
{
    int Order => 0;
    ValueTask InterceptAsync(TEvent evt, CancellationToken cancellationToken = default);
}

/// <summary>Runs after a non-canceled dispatch; never changes the outcome.</summary>
public interface IEventObserver<in TEvent> where TEvent : IAgnesEvent
{
    ValueTask ObserveAsync(TEvent evt, CancellationToken cancellationToken = default);
}

/// <summary>
/// A plugin's hook to bind interceptors/observers onto a bus. Non-generic so a plugin's bindings can be
/// collected uniformly (the generic <see cref="IEventInterceptor{T}"/>/<see cref="IEventObserver{T}"/>
/// closed types aren't enumerable through DI). The binding adds each registration's <see cref="IDisposable"/>
/// to <paramref name="registrations"/> so the caller can unbind them when the plugin is disabled.
/// </summary>
public interface IEventBinding
{
    void Bind(IEventBus bus, ICollection<IDisposable> registrations);
}

/// <summary>
/// An in-process event spine: actions are dispatched as events; handlers observe them or intercept them
/// (mutate/cancel) before they commit. One instance per app (host and client each have their own).
/// </summary>
public interface IEventBus
{
    /// <summary>Runs matching interceptors in <c>Order</c> (each may mutate/cancel); once a
    /// <see cref="CancelableEvent"/> is canceled, remaining interceptors are skipped. Returns the
    /// (possibly-mutated) event so the caller can check <see cref="CancelableEvent.IsCanceled"/>. Observers
    /// run afterward only if it wasn't canceled.</summary>
    Task<TEvent> DispatchAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default) where TEvent : IAgnesEvent;

    /// <summary>Registers an interceptor at runtime; dispose the handle to unregister.</summary>
    IDisposable Intercept<TEvent>(IEventInterceptor<TEvent> interceptor) where TEvent : IAgnesEvent;

    /// <summary>Registers an observer at runtime; dispose the handle to unregister.</summary>
    IDisposable Observe<TEvent>(IEventObserver<TEvent> observer) where TEvent : IAgnesEvent;
}

/// <summary>
/// Default thread-safe <see cref="IEventBus"/>. Handlers are matched by assignability (an interceptor for
/// a base event type runs for derived events, per the contravariant interfaces). Observer exceptions are
/// isolated so one failing observer can't abort the action or the others.
/// <para>
/// The same bus carries both the vetoable command spine and the high-frequency inbound agent-event stream
/// (every <c>SessionEvent</c> is dispatched here). To keep that hot path cheap, the matched+ordered handler
/// set is cached per concrete event type; the cache is invalidated (by a generation bump) only when a
/// handler is registered or unregistered — rare, versus dispatch which is per event. A dispatch for a type
/// with no matching handlers therefore costs a dictionary lookup, not a locked scan of every registration.
/// </para>
/// </summary>
public sealed class EventBus(Action<Exception>? onObserverError = null) : IEventBus
{
    private readonly object _gate = new();
    private readonly List<Registration> _interceptors = [];
    private readonly List<Registration> _observers = [];
    private readonly Action<Exception>? _onObserverError = onObserverError;

    // Matched+ordered handlers per concrete event type, rebuilt lazily when the registration generation
    // changes. ConcurrentDictionary so warm dispatches read without taking _gate.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Match> _cache = new();
    private long _generation;   // bumped under _gate on every register/unregister
    private long _sequence;     // monotonic registration id, for a stable Order tiebreak

    public async Task<TEvent> DispatchAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : IAgnesEvent
    {
        var match = MatchFor(evt!.GetType());

        foreach (var interceptor in match.Interceptors)
        {
            await interceptor.Invoke(evt, cancellationToken).ConfigureAwait(false);
            if (evt is CancelableEvent { IsCanceled: true })
            {
                return evt; // vetoed — skip remaining interceptors and all observers
            }
        }

        foreach (var observer in match.Observers)
        {
            try
            {
                await observer.Invoke(evt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onObserverError?.Invoke(ex);
            }
        }

        return evt;
    }

    // Returns the cached matched handler set for a concrete event type, recomputing under the lock only when
    // the cache is empty for this type or a registration change has bumped the generation since it was built.
    private Match MatchFor(Type actualType)
    {
        if (_cache.TryGetValue(actualType, out var cached) && cached.Generation == Interlocked.Read(ref _generation))
        {
            return cached;
        }

        Match rebuilt;
        lock (_gate)
        {
            rebuilt = new Match(
                _generation,
                _interceptors.Where(r => r.EventType.IsAssignableFrom(actualType)).OrderBy(r => r.Order).ThenBy(r => r.Sequence).ToArray(),
                _observers.Where(r => r.EventType.IsAssignableFrom(actualType)).OrderBy(r => r.Sequence).ToArray());
        }

        _cache[actualType] = rebuilt;
        return rebuilt;
    }

    public IDisposable Intercept<TEvent>(IEventInterceptor<TEvent> interceptor) where TEvent : IAgnesEvent
        => Add(_interceptors, typeof(TEvent), interceptor.Order,
            (evt, ct) => interceptor.InterceptAsync((TEvent)evt, ct));

    public IDisposable Observe<TEvent>(IEventObserver<TEvent> observer) where TEvent : IAgnesEvent
        => Add(_observers, typeof(TEvent), 0,
            (evt, ct) => observer.ObserveAsync((TEvent)evt, ct));

    private IDisposable Add(List<Registration> list, Type eventType, int order, Func<IAgnesEvent, CancellationToken, ValueTask> invoke)
    {
        Registration registration;
        lock (_gate)
        {
            registration = new Registration(eventType, order, ++_sequence, invoke);
            list.Add(registration);
            _generation++; // invalidate the per-type cache
        }

        return new Unsubscriber(() =>
        {
            lock (_gate)
            {
                if (list.Remove(registration))
                {
                    _generation++;
                }
            }
        });
    }

    private sealed record Registration(Type EventType, int Order, long Sequence, Func<IAgnesEvent, CancellationToken, ValueTask> Invoke);

    private sealed record Match(long Generation, Registration[] Interceptors, Registration[] Observers);

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
