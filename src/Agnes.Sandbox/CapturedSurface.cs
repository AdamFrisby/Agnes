namespace Agnes.Sandbox;

/// <summary>
/// The framebuffer *inside* a capture backend: applies damage rectangles, keeps the whole current
/// surface for <see cref="IDisplaySession.SnapshotAsync"/>, and turns each one into the
/// <see cref="DisplayUpdate"/> the seam publishes.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <c>Agnes.Host.Display.DisplaySurface</c>, which is the *host's* copy of the same
/// pixels one layer up: that one exists to be encoded and fanned out to watchers, this one exists
/// because <see cref="IDisplaySession.SnapshotAsync"/> has to be answerable from inside the backend.
/// A backend cannot reach into the host, so the two cannot be one class.
/// </para>
/// <para>
/// Deliberately free of any capture backend. It is a pure function of the rectangles applied to it,
/// which is what makes the assembly rules — packing, clipping, resize, sequence numbering — testable
/// without a VM, and what lets a fake source share the exact code the real one runs.
/// </para>
/// <para>
/// Every update it publishes is <em>packed</em>: <c>Stride == Width * 4</c>, whatever stride the
/// backend used. One invariant for consumers is worth a copy in the rare padded case.
/// </para>
/// </remarks>
public sealed class CapturedSurface
{
    private readonly DisplayPixelFormat _format;
    private readonly Lock _lock = new();
    private byte[] _frame = [];
    private long _sequence;

    public CapturedSurface(DisplayPixelFormat format = DisplayPixelFormat.Bgrx32) => _format = format;

    /// <summary>The current surface size, or null until the first scanout has defined one.</summary>
    public DisplayGeometry? Geometry { get; private set; }

    /// <summary>Sequence number of the most recent update. Starts at 0, before anything has arrived.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>
    /// Replaces the surface. A scanout that changes the size reallocates, so a mode-set needs no
    /// separate event: the full-surface update this returns *is* the resize notification, and
    /// <see cref="Geometry"/> is already the new size when a consumer receives it.
    /// </summary>
    /// <param name="mayAdoptPixels">
    /// True when the caller will never touch <paramref name="pixels"/> again, letting an
    /// already-packed buffer be published as-is instead of copied. Worth an explicit flag: at 1280x800
    /// a full-surface frame is 4 MiB, they arrive several hundred times a second under load, and the
    /// copy this avoids was measurably half the cost of handling one.
    /// </param>
    public DisplayUpdate ApplyScanout(
        int width, int height, int stride, byte[] pixels, DateTimeOffset at, bool mayAdoptPixels = false)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        lock (_lock)
        {
            if (Geometry is null || Geometry.Width != width || Geometry.Height != height)
            {
                Geometry = new DisplayGeometry(width, height, _format);
                _frame = new byte[width * height * 4];
            }

            return ApplyLocked(0, 0, width, height, stride, pixels, at, mayAdoptPixels)
                ?? throw new ArgumentException("A scanout could not be applied to the surface it just defined.", nameof(pixels));
        }
    }

    /// <summary>
    /// Applies a damage rectangle. Returns null — rather than throwing — when the rectangle is
    /// malformed, short, or entirely off-surface: a capture backend is reading someone else's
    /// emulator, and a bad frame should cost one dropped update, not the session.
    /// </summary>
    /// <param name="mayAdoptPixels">See <see cref="ApplyScanout"/>.</param>
    public DisplayUpdate? ApplyUpdate(
        int x, int y, int width, int height, int stride, byte[] pixels, DateTimeOffset at, bool mayAdoptPixels = false)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        lock (_lock)
        {
            return ApplyLocked(x, y, width, height, stride, pixels, at, mayAdoptPixels);
        }
    }

    /// <summary>The whole current surface, packed, as a copy the caller may keep.</summary>
    public byte[] Snapshot()
    {
        lock (_lock)
        {
            return _frame.AsSpan().ToArray();
        }
    }

    private DisplayUpdate? ApplyLocked(
        int x, int y, int width, int height, int stride, byte[] pixels, DateTimeOffset at, bool mayAdoptPixels)
    {
        var packedStride = width * 4;
        if (width <= 0 || height <= 0 || stride < packedStride || Geometry is not { } geometry)
        {
            return null;
        }

        if (pixels.Length < ((height - 1) * stride) + packedStride)
        {
            return null;
        }

        byte[] packed;
        if (stride == packedStride && pixels.Length == packedStride * height)
        {
            packed = mayAdoptPixels ? pixels : pixels.AsSpan().ToArray();
        }
        else
        {
            packed = new byte[packedStride * height];
            for (var row = 0; row < height; row++)
            {
                pixels.AsSpan(row * stride, packedStride).CopyTo(packed.AsSpan(row * packedStride));
            }
        }

        // Clip into the surface. A backend may legitimately describe a rectangle that hangs off the
        // edge — most obviously in the moment between a guest mode-set and the scanout that reports it.
        var clippedX = Math.Max(0, x);
        var clippedY = Math.Max(0, y);
        var columns = Math.Min(width - (clippedX - x), geometry.Width - clippedX);
        var rows = Math.Min(height - (clippedY - y), geometry.Height - clippedY);
        if (columns <= 0 || rows <= 0)
        {
            return null;
        }

        for (var row = 0; row < rows; row++)
        {
            var source = ((row + (clippedY - y)) * packedStride) + ((clippedX - x) * 4);
            var destination = (((clippedY + row) * geometry.Width) + clippedX) * 4;
            packed.AsSpan(source, columns * 4).CopyTo(_frame.AsSpan(destination));
        }

        return new DisplayUpdate(x, y, width, height, packedStride, _format, packed, Interlocked.Increment(ref _sequence), at);
    }
}

/// <summary>Coordinate helpers for the display seam.</summary>
public static class DisplayGeometryExtensions
{
    /// <summary>
    /// Brings a coordinate onto the surface. Injection clamps rather than rejecting because the
    /// alternative — an error a model has to notice and correct — turns a harmless off-by-one at the
    /// edge of the screen into a stuck agent, and a click at the very edge is a thing people do.
    /// </summary>
    public static (int X, int Y) Clamp(this DisplayGeometry geometry, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return (Math.Clamp(x, 0, Math.Max(0, geometry.Width - 1)), Math.Clamp(y, 0, Math.Max(0, geometry.Height - 1)));
    }
}
