using Avalonia;
using Avalonia.Headless;

namespace Agnes.Screenshots;

/// <summary>AppBuilder for the headless session: real Skia drawing so captured frames have pixels.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<global::Agnes.App.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .UseSkia();
}
