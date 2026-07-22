using Agnes.Abstractions.Events;
using Agnes.Ui.Core.Plugins;

namespace Agnes.Ui.Core.Tests;

/// <summary>A plugin can define its OWN event types and receivers and use the shared bus, without those
/// events being baked into the core (see .ideas/00d-event-spine-and-ui-extensibility.md). The bus is
/// generic — it routes any IAgnesEvent — and is exposed to plugins during registration.</summary>
public class PluginDefinedEventTests
{
    // A plugin-defined event + interceptor + observer — types the core knows nothing about.
    private sealed class PluginPing(string message) : CancelableEvent
    {
        public string Message { get; set; } = message;
    }

    private sealed class PluginObserver(List<string> sink) : IEventObserver<PluginPing>
    {
        public ValueTask ObserveAsync(PluginPing evt, CancellationToken ct = default) { sink.Add(evt.Message); return ValueTask.CompletedTask; }
    }

    private sealed class PluginInterceptor : IEventInterceptor<PluginPing>
    {
        public ValueTask InterceptAsync(PluginPing evt, CancellationToken ct = default) { evt.Message = "[seen] " + evt.Message; return ValueTask.CompletedTask; }
    }

    // A plugin module that, during registration, wires handlers for its own event onto the exposed bus.
    private sealed class SelfEventModule(List<string> sink) : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector)
        {
            collector.Bus.Intercept(new PluginInterceptor());
            collector.Bus.Observe(new PluginObserver(sink));
        }
    }

    [Fact]
    public async Task A_plugin_defines_dispatches_and_handles_its_own_event()
    {
        var sink = new List<string>();
        var plugins = ClientPluginHost.FromModules([new SelfEventModule(sink)]);

        // Anyone holding the bus — including the plugin itself — can dispatch the plugin-defined event.
        var result = await plugins.EventBus.DispatchAsync(new PluginPing("hello"));

        Assert.Equal("[seen] hello", result.Message);   // the plugin's own interceptor mutated it
        Assert.Equal(["[seen] hello"], sink);            // the plugin's own observer received it
    }

    [Fact]
    public void The_collector_bus_and_the_built_sets_bus_are_the_same_instance()
    {
        // So handlers registered during Register() see events dispatched on the built set's bus.
        var collector = new ClientPluginCollector();
        var set = collector.Build();
        Assert.Same(collector.Bus, set.EventBus);
    }
}
