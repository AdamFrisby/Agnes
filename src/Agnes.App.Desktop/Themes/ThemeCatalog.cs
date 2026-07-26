using System.Collections.Generic;
using System.Linq;
using Avalonia.Styling;

namespace Agnes.App.Desktop.Themes;

/// <summary>One selectable theme: what it's called, and the variant resource lookups run against.</summary>
/// <param name="Id">Stable id, persisted in settings. Never localised, never renamed.</param>
/// <param name="Name">What the picker shows.</param>
/// <param name="Variant">The variant to hand to <c>Application.RequestedThemeVariant</c>, or null to
/// follow the OS.</param>
/// <param name="PaletteKey">Resource key of this theme's <c>ColorPaletteResources</c>, or null to use
/// the built-in palette. See <see cref="ThemeManager"/> for why a flavour can't register its own.</param>
public sealed record AppTheme(string Id, string Name, ThemeVariant? Variant, string? PaletteKey = null);

/// <summary>
/// The themes this head offers. Each one beyond the built-ins is an Avalonia <see cref="ThemeVariant"/>
/// that <i>inherits</i> Dark or Light, so a theme only has to define the colours it wants to change and
/// every other lookup falls through to the built-in theme rather than coming out unset. That is what
/// keeps a theme file to a list of colours (see <c>Spacegray.axaml</c>) instead of a full palette.
///
/// Adding a theme is three things and no code beyond this file: a variant here, a colour set in a
/// theme dictionary keyed by that variant, and a <c>ColorPaletteResources</c> next to it so Fluent's
/// own chrome follows too. Both of the latter live in the theme's own .axaml — see Spacegray.axaml.
/// </summary>
public static class ThemeCatalog
{
    public static ThemeVariant Spacegray { get; } = new("Spacegray", ThemeVariant.Dark);
    public static ThemeVariant SpacegrayLight { get; } = new("Spacegray Light", ThemeVariant.Light);
    public static ThemeVariant SpacegrayEighties { get; } = new("Spacegray Eighties", ThemeVariant.Dark);
    public static ThemeVariant SpacegrayMocha { get; } = new("Spacegray Mocha", ThemeVariant.Dark);

    /// <summary>Every theme on offer, in picker order: follow-the-OS first, then the two built-ins,
    /// then the ported flavours.</summary>
    public static IReadOnlyList<AppTheme> All { get; } =
    [
        new("System", "System", null),
        new("Light", "Light", ThemeVariant.Light),
        new("Dark", "Dark", ThemeVariant.Dark),
        new("Spacegray", "Spacegray", Spacegray, "SpacegrayPalette"),
        new("SpacegrayLight", "Spacegray Light", SpacegrayLight, "SpacegrayLightPalette"),
        new("SpacegrayEighties", "Spacegray Eighties", SpacegrayEighties, "SpacegrayEightiesPalette"),
        new("SpacegrayMocha", "Spacegray Mocha", SpacegrayMocha, "SpacegrayMochaPalette"),
    ];

    /// <summary>Resolves a persisted id, falling back to System for anything unrecognised — a settings
    /// file written by a newer build (or hand-edited) must not leave the app themeless.</summary>
    public static AppTheme Resolve(string? id)
        => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
