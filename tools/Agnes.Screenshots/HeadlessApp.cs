using Avalonia;
using Avalonia.Headless;

namespace Agnes.Screenshots;

/// <summary>AppBuilder for the headless session: real Skia drawing so captured frames have pixels.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<global::Agnes.App.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // Match the desktop app: the bundled Inter has broken weight matching (400 renders bold), so
            // rely on the platform system sans via Skia/fontconfig, which weighs correctly. Keeps the
            // captured screenshots faithful to what ships.
            .UseSkia();
}
