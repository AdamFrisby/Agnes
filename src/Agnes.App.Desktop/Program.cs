using Avalonia;

namespace Agnes.App.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // NOTE: the bundled Avalonia.Fonts.Inter has broken weight matching on Avalonia 12.1 — its
            // "Inter" family renders weight 400 (Normal) with a bold face (400 and 700 look identical),
            // so all body text came out bold. Use the platform's system sans-serif, which weighs correctly.
            .LogToTrace();
}
