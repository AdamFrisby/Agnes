using Agnes.Abstractions;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>A client-side favorite model, keyed by <c>(AgentId, ModelId)</c>. Pure client state — the host
/// has no involvement (see <c>.ideas/providers/05-model-and-engine-selection.md</c>), so it works identically
/// for a user-configured custom backend the host doesn't specially know about.</summary>
public sealed record ModelFavorite(string AgentId, string ModelId);

/// <summary>One row the model picker can render: a catalogued model, or a favorite that is no longer in the
/// catalog (<see cref="IsAvailable"/> = false). <see cref="IsFavorite"/> drives the favorite toggle's state.</summary>
public sealed record ModelOption(
    string Id,
    string DisplayName,
    bool IsCustomEntryAllowed,
    bool IsFavorite,
    bool IsAvailable);

/// <summary>
/// Reconciles a client's favorites against an agent's current model catalog. Pure over its inputs (no UI, no
/// I/O), so the "a stale favorite is surfaced as unavailable rather than silently offered" rule is a single
/// unit-testable function: catalog models are offered as available (flagged favorite when the user starred
/// them), and any favorite whose id is absent from the catalog is appended as a visibly unavailable row.
/// </summary>
public static class ModelCatalogReconciler
{
    public static IReadOnlyList<ModelOption> Reconcile(
        string agentId,
        IReadOnlyList<ModelInfo> catalog,
        IReadOnlyCollection<ModelFavorite> favorites)
    {
        var favoriteIds = favorites
            .Where(f => f.AgentId == agentId)
            .Select(f => f.ModelId)
            .ToHashSet(StringComparer.Ordinal);

        var options = new List<ModelOption>(catalog.Count);
        var catalogIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in catalog)
        {
            catalogIds.Add(model.Id);
            options.Add(new ModelOption(
                model.Id, model.DisplayName, model.IsCustomEntryAllowed,
                IsFavorite: favoriteIds.Contains(model.Id), IsAvailable: true));
        }

        // A favorite the provider has since removed: shown as a no-longer-available row, never as a working one.
        foreach (var stale in favoriteIds.Where(id => !catalogIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
        {
            options.Add(new ModelOption(
                stale, $"{stale} (no longer available)", IsCustomEntryAllowed: false,
                IsFavorite: true, IsAvailable: false));
        }

        return options;
    }
}
