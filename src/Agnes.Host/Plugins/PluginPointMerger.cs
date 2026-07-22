using Agnes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agnes.Host.Plugins;

/// <summary>
/// Non-generic seam so <see cref="PluginInstaller"/> can merge/remove a loaded plugin's instances into
/// every known plugin-point registry without knowing their element types at compile time — adding
/// support for a brand new plugin-point interface is exactly one more DI registration of
/// <see cref="PluginPointMerger{T}"/>, no changes to <see cref="PluginInstaller"/> itself (AC1).
/// </summary>
public interface IPluginPointMerger
{
    /// <summary>Pulls every <c>T</c> the plugin's own <see cref="IAgnesPluginModule.ConfigureServices"/>
    /// registered out of <paramref name="pluginServices"/> and merges them into the matching
    /// <see cref="IMutablePluginRegistry{T}"/>.</summary>
    void MergeFrom(IServiceProvider pluginServices, string pluginId);

    /// <summary>Removes everything <see cref="MergeFrom"/> previously added for this plugin.</summary>
    void RemoveFrom(string pluginId);
}

/// <summary>Merges a loaded plugin's <typeparamref name="TProvider"/> instances into
/// <paramref name="registry"/>, keyed by <paramref name="idSelector"/> — the same id function the
/// built-in <see cref="PluginRegistry{TProvider}"/> registration for this plugin point already uses.</summary>
public sealed class PluginPointMerger<TProvider>(IMutablePluginRegistry<TProvider> registry, Func<TProvider, string> idSelector)
    : IPluginPointMerger
    where TProvider : notnull
{
    private readonly Dictionary<string, List<string>> _idsByPlugin = new();

    public void MergeFrom(IServiceProvider pluginServices, string pluginId)
    {
        var instances = pluginServices.GetServices<TProvider>().ToArray();
        if (instances.Length == 0)
        {
            return;
        }

        var ids = new List<string>();
        foreach (var instance in instances)
        {
            var id = idSelector(instance);
            registry.Register(id, instance);
            ids.Add(id);
        }

        _idsByPlugin[pluginId] = ids;
    }

    public void RemoveFrom(string pluginId)
    {
        if (_idsByPlugin.Remove(pluginId, out var ids))
        {
            foreach (var id in ids)
            {
                registry.Unregister(id);
            }
        }
    }
}
