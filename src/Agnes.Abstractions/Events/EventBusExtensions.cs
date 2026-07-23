namespace Agnes.Abstractions.Events;

/// <summary>Ergonomic helpers over <see cref="IEventBus"/> for the common veto-gate shape.</summary>
public static class EventBusExtensions
{
    /// <summary>
    /// Dispatches a cancelable action event and reports whether it survived — <c>true</c> if no interceptor
    /// vetoed it, <c>false</c> if one called <see cref="CancelableEvent.Cancel"/>. Centralizes only the
    /// dispatch-and-check; the caller keeps whatever it wants to do on a veto (return, throw, emit a notice,
    /// build an error result), so per-call semantics stay explicit at the call site:
    /// <code>if (!await _bus.AllowsAsync(new BeforeThingEvent(...))) return;</code>
    /// For actions that also read a mutated payload back (a rewritten prompt, an overridden mode), call
    /// <see cref="IEventBus.DispatchAsync"/> directly and read the returned event instead.
    /// </summary>
    public static async Task<bool> AllowsAsync<TEvent>(this IEventBus bus, TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : CancelableEvent
    {
        var result = await bus.DispatchAsync(evt, cancellationToken).ConfigureAwait(false);
        return !result.IsCanceled;
    }
}
