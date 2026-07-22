using Agnes.Abstractions.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Agnes.Host.Plugins;

/// <summary>
/// Applies a loaded plugin's <see cref="IEventBinding"/>s to the host event bus (see
/// <c>.ideas/00d-event-spine-and-ui-extensibility.md</c>), tracking the resulting registrations so they're
/// disposed — unbinding the plugin's interceptors/observers — when the plugin is disabled or uninstalled
/// (AC4). Same <see cref="IPluginPointMerger"/> seam every other plugin-point uses, so a plugin binding
/// events is exactly one more registration in <c>Program.cs</c>, no installer changes.
/// </summary>
public sealed class EventBindingMerger(IEventBus bus) : IPluginPointMerger
{
    private readonly Dictionary<string, List<IDisposable>> _byPlugin = new();
    private readonly object _gate = new();

    public void MergeFrom(IServiceProvider pluginServices, string pluginId)
    {
        var bindings = pluginServices.GetServices<IEventBinding>().ToArray();
        if (bindings.Length == 0)
        {
            return;
        }

        var registrations = new List<IDisposable>();
        foreach (var binding in bindings)
        {
            binding.Bind(bus, registrations);
        }

        lock (_gate) { _byPlugin[pluginId] = registrations; }
    }

    public void RemoveFrom(string pluginId)
    {
        List<IDisposable>? registrations;
        lock (_gate)
        {
            _byPlugin.Remove(pluginId, out registrations);
        }

        if (registrations is not null)
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }
    }
}
