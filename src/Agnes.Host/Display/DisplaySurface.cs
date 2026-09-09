using Agnes.Sandbox;

namespace Agnes.Host.Display;

/// <summary>A rectangle in guest pixels. Damage is unioned rather than listed: one rectangle per frame is
/// what the wire carries, and a union is always correct where a list would have to be exact.</summary>
public readonly record struct DisplayRect(int X, int Y, int Width, int Height)
{
    public static readonly DisplayRect Empty = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

    public DisplayRect Union(DisplayRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(X + Width, other.X + other.Width);
        var bottom = Math.Max(Y + Height, other.Y + other.Height);
        return new DisplayRect(left, top, right - left, bottom - top);
    }

    /// <summary>Clamped to a surface of the given size (a guest may report damage past the edge).</summary>
    public DisplayRect ClampTo(int width, int height)
    {
        var left = Math.Clamp(X, 0, width);
        var top = Math.Clamp(Y, 0, height);
        var right = Math.Clamp(X + Width, 0, width);
        var bottom = Math.Clamp(Y + Height, 0, height);
        return new DisplayRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

/// <summary>
/// The host's copy of the guest's screen: one BGRA buffer that every damage update is blitted into, and
/// that every consumer (a watching person, the agent's screenshot) reads from. There is exactly one, because
/// there is exactly one <see cref="IDisplaySession"/> — two buffers would be two answers to "what is on
/// screen right now".
/// <para>
/// Guest pixels never leave this class as anything but the JPEG encoder's input. Whatever the guest draws
/// is untrusted content; it is copied, not parsed.
/// </para>
/// </summary>
public sealed class DisplaySurface
{
    private readonly object _gate = new();
    private readonly byte[] _pixels;

    public DisplaySurface(DisplayGeometry geometry)
    {
        var bytes = (long)geometry.Width * geometry.Height * 4;
        if (geometry.Width <= 0 || geometry.Height <= 0 || bytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry), "The display's geometry cannot be buffered.");
        }

        Geometry = geometry;
        _pixels = new byte[(int)bytes];
    }

    public DisplayGeometry Geometry { get; }

    public int Width => Geometry.Width;

    public int Height => Geometry.Height;

    /// <summary>Bumped on every applied update; a subscriber that sees the same version has nothing to send.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Blits one damaged region in, converting to BGRA once here so no consumer has to know the guest's
    /// pixel order. Returns the clamped rectangle actually written (empty when the update fell off the
    /// surface entirely), which is what the caller reports as damage.
    /// </summary>
    public DisplayRect Apply(DisplayUpdate update)
    {
        var rect = new DisplayRect(update.X, update.Y, update.Width, update.Height).ClampTo(Width, Height);
        if (rect.IsEmpty)
        {
            return DisplayRect.Empty;
        }

        var source = update.Pixels.Span;
        var swap = update.Format == DisplayPixelFormat.Rgbx32;

        lock (_gate)
        {
            for (var row = 0; row < rect.Height; row++)
            {
                var sourceRow = (row + (rect.Y - update.Y)) * update.Stride + (rect.X - update.X) * 4;
                var destinationRow = ((rect.Y + row) * Width + rect.X) * 4;
                if (sourceRow < 0 || sourceRow + rect.Width * 4 > source.Length)
                {
                    // A short or misdescribed update: keep what we have rather than reading past the buffer.
                    return DisplayRect.Empty;
                }

                var line = source.Slice(sourceRow, rect.Width * 4);
                var target = _pixels.AsSpan(destinationRow, rect.Width * 4);
                line.CopyTo(target);
                if (swap)
                {
                    for (var i = 0; i + 3 < target.Length; i += 4)
                    {
                        (target[i], target[i + 2]) = (target[i + 2], target[i]);
                    }
                }

                // The guest's top byte is "unused", not "opaque"; force alpha so the encoder can't read
                // a fully transparent screen out of a zeroed pad byte.
                for (var i = 3; i < target.Length; i += 4)
                {
                    target[i] = 0xFF;
                }
            }

            Version++;
            return rect;
        }
    }

    /// <summary>Replaces the whole surface from a tightly-packed snapshot (stride = width × 4).</summary>
    public DisplayRect ApplySnapshot(ReadOnlyMemory<byte> pixels, DisplayPixelFormat format)
        => Apply(new DisplayUpdate(0, 0, Width, Height, Width * 4, format, pixels, 0, DateTimeOffset.UtcNow));

    /// <summary>A tightly-packed BGRA copy of one region, taken under the lock so it is one coherent moment
    /// rather than a tear across two guest updates.</summary>
    public byte[] CopyRegion(DisplayRect rect)
    {
        var clamped = rect.ClampTo(Width, Height);
        if (clamped.IsEmpty)
        {
            return [];
        }

        var buffer = new byte[clamped.Width * clamped.Height * 4];
        lock (_gate)
        {
            for (var row = 0; row < clamped.Height; row++)
            {
                var sourceRow = ((clamped.Y + row) * Width + clamped.X) * 4;
                _pixels.AsSpan(sourceRow, clamped.Width * 4).CopyTo(buffer.AsSpan(row * clamped.Width * 4));
            }
        }

        return buffer;
    }

    /// <summary>A copy of the whole surface.</summary>
    public byte[] CopyAll() => CopyRegion(new DisplayRect(0, 0, Width, Height));
}
