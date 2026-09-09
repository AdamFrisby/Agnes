using SkiaSharp;

namespace Agnes.Host.Display;

/// <summary>
/// The one place guest pixels become a picture. Encoding happens on the <b>host</b>, not in the guest and
/// not in the client: the guest runs no Agnes software at all (that is the point of the capture seam), and a
/// client that had to decode a raw framebuffer would need a codec per guest — where a JPEG is decoded by
/// every browser, phone and model already in the loop.
/// <para>
/// Scaling is done here too, so "the subscriber asked for 640 wide" is one transform in one place. The wire
/// header always states the <em>guest</em> geometry, so a client that scaled down still knows what a
/// coordinate means.
/// </para>
/// </summary>
public static class DisplayJpeg
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// Encodes a tightly-packed BGRA buffer as JPEG, optionally scaled so its width is at most
    /// <paramref name="maxWidth"/> (aspect preserved; never scaled <em>up</em> — a phone asking for 4000px
    /// gets the real thing, not a blurry enlargement).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int? maxWidth, int quality)
    {
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, bgra);
        var (targetWidth, targetHeight) = Fit(width, height, maxWidth);

        if (targetWidth == width && targetHeight == height)
        {
            using var direct = image.Encode(SKEncodedImageFormat.Jpeg, Clamp(quality));
            return direct.ToArray();
        }

        using var surface = SKSurface.Create(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        surface.Canvas.Clear(SKColors.Black);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, targetWidth, targetHeight), Sampling);
        using var scaled = surface.Snapshot();
        using var data = scaled.Encode(SKEncodedImageFormat.Jpeg, Clamp(quality));
        return data.ToArray();
    }

    /// <summary>
    /// Tiles frames into ONE contact sheet, left to right then top to bottom, on a black ground with a
    /// hairline gutter. One image rather than N is the whole point: a model judging motion — did the spinner
    /// stop, did the dialog land — reasons far better about a strip it can see at once than about five
    /// separate attachments it has to hold in mind, and it costs a fraction of the tokens.
    /// </summary>
    /// <returns>The JPEG, and the grid it used (columns × rows), so the caller can name it in the text block.</returns>
    public static (byte[] Jpeg, int Columns, int Rows, int CellWidth, int CellHeight) ContactSheet(
        IReadOnlyList<byte[]> frameJpegs, int quality)
    {
        if (frameJpegs.Count == 0)
        {
            return ([], 0, 0, 0, 0);
        }

        var images = new List<SKImage>(frameJpegs.Count);
        try
        {
            foreach (var jpeg in frameJpegs)
            {
                // Decoding our own encoder's output back is deliberate: the burst primitive produces JPEGs
                // (they are what a single-frame screenshot returns too), and re-decoding keeps one code path
                // rather than a parallel "raw frames" one that could drift out of step with it.
                if (SKImage.FromEncodedData(jpeg) is { } image)
                {
                    images.Add(image);
                }
            }

            if (images.Count == 0)
            {
                return ([], 0, 0, 0, 0);
            }

            var columns = (int)Math.Ceiling(Math.Sqrt(images.Count));
            var rows = (int)Math.Ceiling(images.Count / (double)columns);
            var cellWidth = images.Max(i => i.Width);
            var cellHeight = images.Max(i => i.Height);
            const int Gutter = 2;

            var sheetWidth = columns * cellWidth + (columns - 1) * Gutter;
            var sheetHeight = rows * cellHeight + (rows - 1) * Gutter;

            using var surface = SKSurface.Create(new SKImageInfo(sheetWidth, sheetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
            surface.Canvas.Clear(SKColors.Black);
            for (var i = 0; i < images.Count; i++)
            {
                var column = i % columns;
                var row = i / columns;
                var x = column * (cellWidth + Gutter);
                var y = row * (cellHeight + Gutter);
                surface.Canvas.DrawImage(images[i], new SKRect(x, y, x + cellWidth, y + cellHeight), Sampling);
            }

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, Clamp(quality));
            return (data.ToArray(), columns, rows, cellWidth, cellHeight);
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>The size <paramref name="width"/>×<paramref name="height"/> becomes under a max-width cap.</summary>
    public static (int Width, int Height) Fit(int width, int height, int? maxWidth)
    {
        if (maxWidth is not { } cap || cap <= 0 || cap >= width)
        {
            return (width, height);
        }

        var scaled = (int)Math.Round(height * (cap / (double)width));
        return (cap, Math.Max(1, scaled));
    }

    private static int Clamp(int quality) => Math.Clamp(quality, 1, 100);
}
