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
/// runs first.</summary>
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
/// </summary>
public sealed class EventBus(Action<Exception>? onObserverError = null) : IEventBus
{
    private readonly object _gate = new();
    private readonly List<Registration> _interceptors = [];
    private readonly List<Registration> _observers = [];
    private readonly Action<Exception>? _onObserverError = onObserverError;

    public async Task<TEvent> DispatchAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : IAgnesEvent
    {
        var actualType = evt!.GetType();

        Registration[] interceptors;
        lock (_gate)
        {
            interceptors = _interceptors
                .Where(r => r.EventType.IsAssignableFrom(actualType))
                .OrderBy(r => r.Order)
                .ToArray();
        }

        foreach (var interceptor in interceptors)
        {
            await interceptor.Invoke(evt, cancellationToken).ConfigureAwait(false);
            if (evt is CancelableEvent { IsCanceled: true })
            {
                return evt; // vetoed — skip remaining interceptors and all observers
            }
        }

        Registration[] observers;
        lock (_gate)
        {
            observers = _observers.Where(r => r.EventType.IsAssignableFrom(actualType)).ToArray();
        }

        foreach (var observer in observers)
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

    public IDisposable Intercept<TEvent>(IEventInterceptor<TEvent> interceptor) where TEvent : IAgnesEvent
        => Add(_interceptors, new Registration(typeof(TEvent), interceptor.Order,
            (evt, ct) => interceptor.InterceptAsync((TEvent)evt, ct)));

    public IDisposable Observe<TEvent>(IEventObserver<TEvent> observer) where TEvent : IAgnesEvent
        => Add(_observers, new Registration(typeof(TEvent), 0,
            (evt, ct) => observer.ObserveAsync((TEvent)evt, ct)));

    private IDisposable Add(List<Registration> list, Registration registration)
    {
        lock (_gate) { list.Add(registration); }
        return new Unsubscriber(() => { lock (_gate) { list.Remove(registration); } });
    }

    private sealed record Registration(Type EventType, int Order, Func<IAgnesEvent, CancellationToken, ValueTask> Invoke);

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
