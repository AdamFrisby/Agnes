using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Agnes.App.Desktop.Themes;

/// <summary>
/// Applies a theme from <see cref="ThemeCatalog"/> to the running application.
///
/// Two halves have to move together. The app's own roles live in theme dictionaries keyed by
/// <see cref="ThemeVariant"/>, so setting <c>RequestedThemeVariant</c> is enough for them — a flavour
/// inherits Dark or Light, and anything it doesn't name falls through.
///
/// Fluent's stock control chrome is the awkward half. <c>FluentTheme.Palettes</c> is the supported way
/// to retint it (it reaches Fluent's neutral ramp, which app-level overrides can't, because Fluent
/// aliases that ramp internally with <c>StaticResource</c>) — but the collection rejects any key that
/// isn't Light or Dark:
///
///   <c>FluentTheme.Palettes only supports Light and Dark variants.</c>
///
/// So a flavour's palette can't be registered under its own variant. Instead it's declared as a plain
/// resource (see <c>Spacegray.axaml</c>) and assigned here into the slot the flavour inherits, with the
/// built-in palette restored when a built-in theme is selected again. Only one theme is ever active, so
/// borrowing the slot is safe; the alternative is stock controls falling back to Fluent's own greys
/// while everything around them is on the flavour's palette.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<ThemeVariant, ColorPaletteResources> BuiltInPalettes = [];

    /// <summary>Applies a theme by its persisted id. Unknown ids resolve to System.</summary>
    public static void Apply(string? themeId)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var theme = ThemeCatalog.Resolve(themeId);
        ApplyPalette(app, theme);
        app.RequestedThemeVariant = theme.Variant ?? ThemeVariant.Default;
        System.Console.WriteLine($"[theme] id={themeId} variant={theme.Variant?.Key} palette={theme.PaletteKey}");
        app.Resources.TryGetResource("BgColor", theme.Variant, out var bg);
        System.Console.WriteLine($"[theme] BgColor={bg}");
    }

    /// <summary>
    /// Puts the right <see cref="ColorPaletteResources"/> in the Light and Dark slots for this theme:
    /// the flavour's own if it has one, else the built-in palette that shipped in App.axaml.
    /// </summary>
    private static void ApplyPalette(Application app, AppTheme theme)
    {
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent)
        {
            return;
        }

        // Capture what App.axaml declared, once, before anything overwrites it.
        if (BuiltInPalettes.Count == 0)
        {
            foreach (var (variant, palette) in fluent.Palettes)
            {
                BuiltInPalettes[variant] = palette;
            }
        }

        // A flavour inherits exactly one of Light/Dark, and that's the slot its palette goes in.
        var slot = theme.Variant?.InheritVariant ?? theme.Variant;

        var flavour = FindPalette(app, theme.PaletteKey);

        foreach (var variant in BuiltInPalettes.Keys)
        {
            var wanted = Equals(variant, slot) && flavour is not null ? flavour : BuiltInPalettes[variant];
            if (!ReferenceEquals(fluent.Palettes[variant], wanted))
            {
                fluent.Palettes[variant] = wanted;
            }
        }
    }

    /// <summary>The palette a theme declared alongside its colours, or null for the built-in themes.</summary>
    private static ColorPaletteResources? FindPalette(Application app, string? key)
    {
        if (key is null)
        {
            return null;
        }

        // Theme-invariant lookup: a flavour's palette sits beside its colour sets, not inside one.
        app.Resources.TryGetResource(key, null, out var found);
        return found as ColorPaletteResources;
    }
}
