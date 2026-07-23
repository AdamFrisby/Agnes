namespace Agnes.Abstractions;

/// <summary>
/// A model an agent's CLI can be told to use. Distinct from <see cref="SessionMode"/> (Ask/Code/Plan) —
/// this is the underlying model axis. <see cref="IsCustomEntryAllowed"/> gates whether the picker also
/// accepts a free-text id in place of a catalogued one (providers ship models faster than a static list
/// can track), so an adapter can lock this down where a free-text id genuinely wouldn't make sense.
/// </summary>
public sealed record ModelInfo(string Id, string DisplayName, bool IsCustomEntryAllowed = true);

/// <summary>
/// Optional capability an <see cref="IAgentAdapter"/> may implement (checked via <c>is IModelListingAdapter</c>)
/// to enumerate the models its CLI accepts. ACP has no standard model-list call, so live probing is optional:
/// <see cref="ListModelsAsync"/> returns null when the CLI can't be asked, and the caller falls back to
/// <see cref="StaticModels"/> — the picker is therefore never empty just because probing isn't supported.
/// </summary>
public interface IModelListingAdapter
{
    /// <summary>Live-probes the provider for currently available models, or null when the CLI can't be asked
    /// (fall back to <see cref="StaticModels"/>).</summary>
    Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Static fallback list, used when live probing isn't supported or returns null.</summary>
    IReadOnlyList<ModelInfo> StaticModels { get; }
}

/// <summary>Resolves an adapter's effective model catalog. Pure over its input, so the live-vs-static rule
/// lives in exactly one place: use the live-probed list when the adapter supplies one, else the static
/// fallback.</summary>
public static class ModelCatalog
{
    public static async Task<IReadOnlyList<ModelInfo>> ResolveAsync(IModelListingAdapter adapter, CancellationToken ct = default)
    {
        var live = await adapter.ListModelsAsync(ct).ConfigureAwait(false);
        return live ?? adapter.StaticModels;
    }
}
