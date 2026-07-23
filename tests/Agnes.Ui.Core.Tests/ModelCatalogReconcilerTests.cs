using Agnes.Abstractions;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public sealed class ModelCatalogReconcilerTests
{
    private static readonly IReadOnlyList<ModelInfo> Catalog =
    [
        new ModelInfo("sonnet", "Claude Sonnet"),
        new ModelInfo("opus", "Claude Opus"),
    ];

    [Fact]
    public void Favorite_present_in_catalog_is_offered_as_available()
    {
        var options = ModelCatalogReconciler.Reconcile(
            "claude-code", Catalog, [new ModelFavorite("claude-code", "opus")]);

        var opus = Assert.Single(options, o => o.Id == "opus");
        Assert.True(opus.IsAvailable);
        Assert.True(opus.IsFavorite);
    }

    [Fact]
    public void Favorite_absent_from_catalog_is_flagged_unavailable()
    {
        var options = ModelCatalogReconciler.Reconcile(
            "claude-code", Catalog, [new ModelFavorite("claude-code", "retired-model")]);

        var stale = Assert.Single(options, o => o.Id == "retired-model");
        Assert.False(stale.IsAvailable);
        Assert.True(stale.IsFavorite);
        Assert.False(stale.IsCustomEntryAllowed); // not a working choice, so not custom-selectable either
        Assert.Contains("no longer available", stale.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_favorite_catalog_models_are_offered_but_not_favorited()
    {
        var options = ModelCatalogReconciler.Reconcile("claude-code", Catalog, []);

        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.True(o.IsAvailable));
        Assert.All(options, o => Assert.False(o.IsFavorite));
    }

    [Fact]
    public void Favorites_for_a_different_agent_are_ignored()
    {
        var options = ModelCatalogReconciler.Reconcile(
            "claude-code", Catalog,
            [new ModelFavorite("opencode", "opus"), new ModelFavorite("opencode", "some-other")]);

        // No favorite flagged, and no stale row appended for the other agent's favorites.
        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.False(o.IsFavorite));
    }
}
