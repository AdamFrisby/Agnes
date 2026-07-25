using Avalonia.Styling;

namespace Agnes.App.Mobile.Services;

/// <summary>Applies the user's theme choice to the running app.</summary>
public static class ThemeApplier
{
    public static void Apply(string theme)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                // "System" follows the OS, which on Android means the device's dark-mode switch.
                _ => ThemeVariant.Default,
            };
        }
    }
}
