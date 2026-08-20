using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace Agnes.App.Desktop.Themes;

/// <summary>One interface font offered by the desktop client.</summary>
/// <param name="Id">Stable id persisted in settings.</param>
/// <param name="Name">Label shown in Appearance settings.</param>
/// <param name="Family">The Avalonia family applied to ordinary UI and transcript text.</param>
public sealed record AppFont(string Id, string Name, FontFamily Family);

/// <summary>The supported interface fonts. Code and terminal text retain the dedicated mono face.</summary>
public static class FontCatalog
{
    private const string FontAssetRoot = "avares://Agnes.App.Desktop/Assets/Fonts";

    public static IReadOnlyList<AppFont> All { get; } =
    [
        new("Manrope", "Manrope", new FontFamily($"{FontAssetRoot}#Manrope")),
        new("System", "System default", FontFamily.Default),
        new("JetBrainsMono", "JetBrains Mono", new FontFamily($"{FontAssetRoot}#JetBrains Mono")),
    ];

    /// <summary>Unknown or absent ids fall back to the branded default.</summary>
    public static AppFont Resolve(string? id)
        => All.FirstOrDefault(font => string.Equals(font.Id, id, System.StringComparison.Ordinal)) ?? All[0];
}

/// <summary>Applies the selected interface font to dynamic app and Fluent font resources.</summary>
public static class FontManager
{
    public static void Apply(string? fontId)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var family = FontCatalog.Resolve(fontId).Family;
        app.Resources["UiFont"] = family;
        app.Resources["ContentControlThemeFontFamily"] = family;
    }
}
