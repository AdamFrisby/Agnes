using System.Reflection;
using Avalonia.Controls;

namespace Agnes.App.Desktop;

/// <summary>Single source for product metadata shown by desktop-native surfaces.</summary>
public static class DesktopBranding
{
    public static string ApplicationName { get; } = "Agnes";
    public static string Description { get; } = "A remote interface to coding CLIs.";
    public static string AboutMenuLabel { get; } = "About Agnes";
    public static string LearnMoreLabel { get; } = "Learn more about Agnes";
    public static Uri RepositoryUri { get; } = new("https://github.com/AdamFrisby/Agnes");

    public static string Version { get; } = GetVersion();
    public static string Copyright { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? $"Copyright (c) {DateTime.UtcNow.Year} Agnes contributors";

    public static NativeMenu CreateApplicationMenu(EventHandler aboutRequested)
    {
        ArgumentNullException.ThrowIfNull(aboutRequested);

        var about = new NativeMenuItem(AboutMenuLabel);
        about.Click += aboutRequested;
        return new NativeMenu { about };
    }

    private static string GetVersion()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+', 2)[0]
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
        return $"Version {version}";
    }
}
