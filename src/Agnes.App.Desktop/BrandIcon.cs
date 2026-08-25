using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Agnes.App.Desktop;

/// <summary>
/// The application icon: the Agnes squid mark, rasterized once from the vector in
/// <c>Themes/BrandMark.axaml</c>.
/// </summary>
/// <remarks>
/// Agnes ships as a bare download with no installer, so nothing outside the process ever registers an
/// icon for it — and a window that sets none falls back to whatever the window manager uses for an
/// unknown client, which on X11 is the generic "X" logo. That was the taskbar entry and the window
/// button, for every window the app opens.
///
/// <para>Rendered from the same vector the chrome draws rather than shipped as a PNG beside it, so the
/// mark cannot drift between the two, and re-rendered at whatever size is asked for instead of scaling
/// one baked bitmap. It is built lazily and cached: rasterizing costs a render pass, and every window —
/// the main one and each tab dragged out into its own — wants the same result.</para>
/// </remarks>
public static class BrandIcon
{
    private const string MarkResource = "AgnesMark";

    private static WindowIcon? _icon;

    /// <summary>The window/taskbar icon, or null if the mark resource can't be found (in which case a
    /// window keeps the platform default rather than the app failing to open).</summary>
    public static WindowIcon? Icon => _icon ??= Render(256);

    /// <summary>Gives <paramref name="window"/> the app icon. Safe to call on any window, and a no-op
    /// when the mark is unavailable.</summary>
    public static void Apply(Window window)
    {
        if (Icon is { } icon)
        {
            window.Icon = icon;
        }
    }

    private static WindowIcon? Render(int size)
    {
        if (Application.Current?.FindResource(MarkResource) is not IImage mark)
        {
            return null;
        }

        // Through an Image control rather than by drawing the geometry directly: the control does the
        // uniform fit from the mark's own bounds into a square, which is what an icon has to be.
        var host = new Image { Source = mark, Stretch = Stretch.Uniform, Width = size, Height = size };
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(host);
        return new WindowIcon(bitmap);
    }
}
