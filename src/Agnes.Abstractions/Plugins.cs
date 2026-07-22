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
/// Default <see cref="IPluginRegistry{TProvider}"/>, built from a set of implementations plus a
/// function that extracts each one's stable id. Different plugin-point interfaces name their id
/// differently (<c>IAgentAdapter.Descriptor.Id</c>, <c>ISandboxProvider.Name</c>, …), so the registry
/// doesn't require them to share a common descriptor shape — it just needs a way to ask each instance
/// for the id it should be found under.
/// </summary>
public sealed class PluginRegistry<TProvider> : IPluginRegistry<TProvider> where TProvider : notnull
{
    private readonly Dictionary<string, TProvider> _byId;

    public PluginRegistry(IEnumerable<TProvider> providers, Func<TProvider, string> idSelector)
    {
        All = providers.ToArray();
        _byId = new Dictionary<string, TProvider>(StringComparer.Ordinal);
        foreach (var provider in All)
        {
            _byId[idSelector(provider)] = provider;
        }
    }

    public IReadOnlyList<TProvider> All { get; }

    public TProvider? Find(string id) => _byId.GetValueOrDefault(id);
}
