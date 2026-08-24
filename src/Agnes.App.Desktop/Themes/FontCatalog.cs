using Avalonia;
using Avalonia.Media;

namespace Agnes.App.Desktop.Themes;

/// <summary>Normalises the free-form interface-font setting and resolves its Avalonia family.</summary>
public static class FontCatalog
{
    private const string FontAssetRoot = "avares://Agnes.App.Desktop/Assets/Fonts";
    public const string Default = "Default";

    /// <summary>Blank input means the Agnes default. Every other value is an installed family name.</summary>
    public static string Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? Default : name.Trim();

    /// <summary>The editable text field is blank for the default and contains the exact custom family.</summary>
    public static string InputValue(string? name)
    {
        var normalized = Normalize(name);
        return normalized is Default or "Manrope" or "System" ? string.Empty
            : normalized == "JetBrainsMono" ? "JetBrains Mono"
            : normalized;
    }

    public static FontFamily Resolve(string? name)
        => Normalize(name) switch
        {
            Default or "Manrope" or "System" => new FontFamily($"{FontAssetRoot}#Manrope"),
            "JetBrainsMono" or "JetBrains Mono" => new FontFamily($"{FontAssetRoot}#JetBrains Mono"),
            var installedFamily => new FontFamily(installedFamily),
        };
}

/// <summary>Applies the selected interface font to dynamic app and Fluent font resources.</summary>
public static class FontManager
{
    public static void Apply(string? fontFamily)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var family = FontCatalog.Resolve(fontFamily);
        app.Resources["UiFont"] = family;
        app.Resources["ContentControlThemeFontFamily"] = family;
    }

    /// <summary>Updates message, transcript-event, and preview text without changing composer or UI chrome.</summary>
    public static void ApplyChatScale(double scale)
    {
        if (Application.Current is { } app)
        {
            foreach (var size in new[] { 11, 12, 13, 15, 17, 20, 24 })
            {
                app.Resources[$"ChatContentFontSize{size}"] = size * scale;
            }
        }
    }
}
