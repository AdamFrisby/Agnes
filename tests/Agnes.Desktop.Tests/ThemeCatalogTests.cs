using Agnes.App.Desktop.Themes;
using Avalonia.Styling;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The theme catalogue is the contract a ported theme is written against, and two of its rules are
/// easy to break silently: a flavour must inherit a built-in variant (or every colour it doesn't
/// name comes out unset), and it must name a palette that actually exists in the merged resources
/// (or Fluent's own controls quietly keep the previous theme's chrome).
/// </summary>
public class ThemeCatalogTests
{
    [Fact]
    public void Resolve_falls_back_to_system_for_unknown_ids()
    {
        // A settings file written by a newer build, or hand-edited, must not leave the app themeless.
        Assert.Equal("System", ThemeCatalog.Resolve("no-such-theme").Id);
        Assert.Equal("System", ThemeCatalog.Resolve(null).Id);
    }

    [Fact]
    public void Every_theme_id_is_unique_and_round_trips()
    {
        Assert.Equal(ThemeCatalog.All.Count, ThemeCatalog.All.Select(t => t.Id).Distinct().Count());

        foreach (var theme in ThemeCatalog.All)
        {
            Assert.Same(theme, ThemeCatalog.Resolve(theme.Id));
        }
    }

    [Fact]
    public void System_follows_the_os_and_the_built_ins_pin_a_variant()
    {
        Assert.Null(ThemeCatalog.Resolve("System").Variant);
        Assert.Equal(ThemeVariant.Light, ThemeCatalog.Resolve("Light").Variant);
        Assert.Equal(ThemeVariant.Dark, ThemeCatalog.Resolve("Dark").Variant);
    }

    [Fact]
    public void Ported_themes_inherit_a_built_in_variant()
    {
        // Inheritance is what keeps a theme file to a list of colours: anything it leaves out falls
        // through to Dark or Light instead of resolving to nothing.
        var ported = ThemeCatalog.All.Where(t => t.PaletteKey is not null).ToList();
        Assert.NotEmpty(ported);

        foreach (var theme in ported)
        {
            var inherit = theme.Variant?.InheritVariant;
            Assert.True(inherit == ThemeVariant.Dark || inherit == ThemeVariant.Light,
                $"{theme.Id} must inherit Dark or Light, but inherits {inherit?.Key.ToString() ?? "nothing"}");
        }
    }

    [Fact]
    public void Only_the_built_in_themes_go_without_a_palette()
    {
        // Fluent's chrome can't follow a theme that doesn't bring a palette with it.
        foreach (var theme in ThemeCatalog.All)
        {
            var isBuiltIn = theme.Id is "System" or "Light" or "Dark";
            Assert.True(isBuiltIn == (theme.PaletteKey is null),
                $"{theme.Id}: built-in themes use the palette in App.axaml, ported ones must name their own");
        }
    }
}
