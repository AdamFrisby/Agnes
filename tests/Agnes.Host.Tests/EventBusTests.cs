using Agnes.Abstractions.Events;

namespace Agnes.Host.Tests;

/// <summary>The event spine: interceptors (ordered, mutate/cancel) then observers
/// (see .ideas/00d-event-spine-and-ui-extensibility.md).</summary>
public class EventBusTests
{
    private sealed class BeforeThing(string payload) : CancelableEvent
    {
        public string Payload { get; set; } = payload;
    }

    private sealed class ThingHappened(string what) : IAgnesEvent
    {
        public string What { get; } = what;
    }

    private sealed class Interceptor(int order, Func<BeforeThing, ValueTask> body) : IEventInterceptor<BeforeThing>
    {
        public int Order => order;
        public ValueTask InterceptAsync(BeforeThing evt, CancellationToken ct = default) => body(evt);
    }

    private sealed class Observer(Action<ThingHappened> body) : IEventObserver<ThingHappened>
    {
        public ValueTask ObserveAsync(ThingHappened evt, CancellationToken ct = default) { body(evt); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Interceptors_run_in_order_and_can_mutate_the_payload()
    {
        var bus = new EventBus();
        var seen = new List<int>();
        bus.Intercept(new Interceptor(2, e => { seen.Add(2); e.Payload += "-b"; return ValueTask.CompletedTask; }));
        bus.Intercept(new Interceptor(1, e => { seen.Add(1); e.Payload += "-a"; return ValueTask.CompletedTask; }));

        var result = await bus.DispatchAsync(new BeforeThing("x"));

        Assert.Equal([1, 2], seen);           // lower Order first
        Assert.Equal("x-a-b", result.Payload); // mutation visible to the caller
        Assert.False(result.IsCanceled);
    }

    [Fact]
    public async Task Cancel_stops_remaining_interceptors_and_suppresses_observers()
    {
        var bus = new EventBus();
        var ranSecond = false;
        var observerRan = false;
        bus.Intercept(new Interceptor(1, e => { e.Cancel("nope"); return ValueTask.CompletedTask; }));
        bus.Intercept(new Interceptor(2, _ => { ranSecond = true; return ValueTask.CompletedTask; }));
        bus.Observe<BeforeThing>(new ObserverB(() => observerRan = true));

        var result = await bus.DispatchAsync(new BeforeThing("x"));

        Assert.True(result.IsCanceled);
        Assert.Equal("nope", result.CancelReason);
        Assert.False(ranSecond);
        Assert.False(observerRan);
    }

    private sealed class ObserverB(Action mark) : IEventObserver<BeforeThing>
    {
        public ValueTask ObserveAsync(BeforeThing evt, CancellationToken ct = default) { mark(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Observers_run_after_a_non_canceled_dispatch()
    {
        var bus = new EventBus();
        string? observed = null;
        bus.Observe(new Observer(e => observed = e.What));

        await bus.DispatchAsync(new ThingHappened("opened"));

        Assert.Equal("opened", observed);
    }

    [Fact]
    public async Task A_throwing_observer_is_isolated_and_does_not_abort_the_others()
    {
        var errors = new List<Exception>();
        var bus = new EventBus(errors.Add);
        var secondRan = false;
        bus.Observe(new Observer(_ => throw new InvalidOperationException("boom")));
        bus.Observe(new Observer(_ => secondRan = true));

        await bus.DispatchAsync(new ThingHappened("x"));

        Assert.True(secondRan);
        Assert.Single(errors);
    }

    [Fact]
    public async Task Disposing_a_registration_stops_it_from_running()
    {
        var bus = new EventBus();
        var count = 0;
        var handle = bus.Observe(new Observer(_ => count++));

        await bus.DispatchAsync(new ThingHappened("a"));
        handle.Dispose();
        await bus.DispatchAsync(new ThingHappened("b"));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Dispatch_with_no_handlers_is_a_no_op_returning_the_event()
    {
        var bus = new EventBus();
        var evt = new BeforeThing("unchanged");
        var result = await bus.DispatchAsync(evt);
        Assert.Same(evt, result);
        Assert.False(result.IsCanceled);
        Assert.Equal("unchanged", result.Payload);
    }
}
