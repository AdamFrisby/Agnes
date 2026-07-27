using Agnes.Abstractions;

namespace Agnes.Host.Hosting;

/// <summary>
/// Fans a browse or a search out across every registered catalogue of a kind and merges the answers. Written
/// once against <see cref="ICatalogProvider{TEntry}"/> and used for both skills and MCP servers, because the
/// hard part is the same for both and neither is improved by having its own copy.
///
/// The hard part is failure. These are network sources: one registry being slow, rate-limited or simply down
/// must not take the others with it, so a provider that throws contributes no results and the search still
/// returns. Staying quiet about that would be worse than the failure — a rate-limited registry would look
/// like an empty one — so what went wrong comes back alongside the hits, in
/// <see cref="CatalogResults{TEntry}.Failures"/>, for the caller to show.
/// </summary>
public static class CatalogSearch
{
    /// <summary>What every source offers before anyone has searched.</summary>
    public static Task<CatalogResults<TEntry>> ListAsync<TEntry>(
        IEnumerable<ICatalogProvider<TEntry>> providers, CancellationToken ct = default)
        => GatherAsync(providers, (p, token) => p.ListAsync(token), ct);

    /// <summary>
    /// Every source's answer to a query. Sources that can't search are still asked — their
    /// <see cref="ICatalogProvider{TEntry}.SearchAsync"/> falls back to listing, which for them is the whole
    /// truth anyway.
    /// </summary>
    public static Task<CatalogResults<TEntry>> SearchAsync<TEntry>(
        IEnumerable<ICatalogProvider<TEntry>> providers, string query, CancellationToken ct = default)
        => GatherAsync(providers, (p, token) => p.SearchAsync(query, token), ct);

    /// <summary>The sources themselves, for a picker.</summary>
    public static IReadOnlyList<CatalogSource> Sources<TEntry>(IEnumerable<ICatalogProvider<TEntry>> providers)
        => providers.Select(p => new CatalogSource(p.Id, p.DisplayName, p.SupportsSearch)).ToArray();

    private static async Task<CatalogResults<TEntry>> GatherAsync<TEntry>(
        IEnumerable<ICatalogProvider<TEntry>> providers,
        Func<ICatalogProvider<TEntry>, CancellationToken, Task<IReadOnlyList<TEntry>>> ask,
        CancellationToken ct)
    {
        var sources = providers.ToArray();
        var answers = await Task.WhenAll(sources.Select(async provider =>
        {
            try
            {
                var entries = await ask(provider, ct).ConfigureAwait(false);
                return (Hits: entries.Select(e => new CatalogHit<TEntry>(provider.Id, provider.DisplayName, e)).ToArray(),
                        Failure: (string?)null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable registry costs its own results and nothing else.
                return (Hits: [], Failure: $"{provider.DisplayName}: {ex.Message}");
            }
        })).ConfigureAwait(false);

        return new CatalogResults<TEntry>(
            answers.SelectMany(a => a.Hits).ToArray(),
            answers.Select(a => a.Failure).OfType<string>().ToArray());
    }
}
