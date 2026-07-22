namespace Agnes.Abstractions;

/// <summary>
/// A typed collection of every implementation the host knows about for one plugin-point interface
/// (e.g. every <see cref="IAgentAdapter"/>). The common lookup-by-id + enumerate surface every plugin
/// consumer needs, instead of each one hand-rolling its own dictionary over an <c>IEnumerable&lt;T&gt;</c>.
/// </summary>
public interface IPluginRegistry<TProvider> where TProvider : notnull
{
    /// <summary>Every registered implementation, in registration order.</summary>
    IReadOnlyList<TProvider> All { get; }

    /// <summary>Looks up a single implementation by its stable id, or null if none matches.</summary>
    TProvider? Find(string id);
}

/// <summary>
/// An <see cref="IPluginRegistry{TProvider}"/> that also accepts providers registered after startup —
/// what <c>IPluginInstaller</c> uses to merge a newly installed/enabled plugin's instances in, and to
/// remove them again on disable/uninstall, without a host restart (AC6/AC8/AC9 of
/// .ideas/00-plugin-architecture.md).
/// </summary>
public interface IMutablePluginRegistry<TProvider> : IPluginRegistry<TProvider> where TProvider : notnull
{
    /// <summary>Adds (or replaces) the provider found under <paramref name="id"/>.</summary>
    void Register(string id, TProvider provider);

    /// <summary>Removes the provider registered under <paramref name="id"/>, if any.</summary>
    void Unregister(string id);
}

/// <summary>
/// Default <see cref="IPluginRegistry{TProvider}"/>, built from a set of implementations plus a
/// function that extracts each one's stable id. Different plugin-point interfaces name their id
/// differently (<c>IAgentAdapter.Descriptor.Id</c>, <c>ISandboxProvider.Name</c>, …), so the registry
/// doesn't require them to share a common descriptor shape — it just needs a way to ask each instance
/// for the id it should be found under. Thread-safe: reads snapshot under a lock, so <see cref="All"/>
/// is safe to enumerate concurrently with a plugin install/uninstall calling
/// <see cref="Register"/>/<see cref="Unregister"/> on another thread.
/// </summary>
public sealed class PluginRegistry<TProvider> : IMutablePluginRegistry<TProvider> where TProvider : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TProvider> _byId;

    public PluginRegistry(IEnumerable<TProvider> providers, Func<TProvider, string> idSelector)
    {
        _byId = new Dictionary<string, TProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            _byId[idSelector(provider)] = provider;
        }
    }

    public IReadOnlyList<TProvider> All
    {
        get { lock (_gate) { return _byId.Values.ToArray(); } }
    }

    public TProvider? Find(string id)
    {
        lock (_gate) { return _byId.GetValueOrDefault(id); }
    }

    public void Register(string id, TProvider provider)
    {
        lock (_gate) { _byId[id] = provider; }
    }

    public void Unregister(string id)
    {
        lock (_gate) { _byId.Remove(id); }
    }
}
