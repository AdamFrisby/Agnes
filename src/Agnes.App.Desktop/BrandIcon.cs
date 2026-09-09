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
///
/// <para>The drawing is composed onto the square here rather than handed to an <see cref="Image"/> to fit,
/// because the mark's geometry does not start at the origin: it is authored on the artwork's native 256
/// grid and its ink actually begins at about (51, 31). <c>DrawingImage.Size</c> reports only the
/// <i>extent</i> of those bounds and drops the offset, so the control centred a box whose contents were
/// then drawn 51 units back — the two cancelled, and the icon sat hard against the left edge with a
/// quarter of the square empty on the right. Mapping the real <c>GetBounds()</c> rect onto the square
/// makes the result independent of that: the ink is centred because its measured box is centred.</para>
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

    /// <summary>
    /// The share of the square left empty on each edge. A window icon is composited against a taskbar or
    /// title bar, so a mark that runs edge to edge reads as cropped; a small, even inset makes it read as
    /// placed. Applied to the fitted box, so it stays proportional at every size.
    /// </summary>
    private const double MarginFraction = 0.04;

    private static WindowIcon? Render(int size)
        => RenderBitmap(size) is { } bitmap ? new WindowIcon(bitmap) : null;

    /// <summary>
    /// Rasterizes the mark, centred, into a square bitmap of <paramref name="size"/> pixels; null when the
    /// mark resource isn't available. Separate from <see cref="Render"/> so the placement can be measured
    /// directly — a <see cref="WindowIcon"/> only serializes itself as an ICO, which is no use for
    /// asserting where the ink actually landed.
    /// </summary>
    public static RenderTargetBitmap? RenderBitmap(int size)
    {
        if (Application.Current?.FindResource(MarkResource) is not DrawingImage { Drawing: { } drawing })
        {
            return null;
        }

        var bounds = drawing.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        // Uniform fit of the mark's true bounds into the inset square, then centred on both axes. The
        // leading translation is the part that matters: it moves the geometry's own origin to zero, so
        // everything after it is reasoning about a box at (0,0) rather than one at (51,31).
        var box = size * (1.0 - (2.0 * MarginFraction));
        var scale = Math.Min(box / bounds.Width, box / bounds.Height);
        var width = bounds.Width * scale;
        var height = bounds.Height * scale;

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(
            Matrix.CreateTranslation(-bounds.X, -bounds.Y)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation((size - width) / 2.0, (size - height) / 2.0)))
        {
            drawing.Draw(context);
        }

        return bitmap;
    }
}
