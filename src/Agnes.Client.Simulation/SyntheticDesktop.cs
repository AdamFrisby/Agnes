namespace Agnes.Client.Simulation;

/// <summary>
/// Draws the fake guest desktop the simulated display streams: a gradient ground, a window, a shape moving on
/// a Lissajous path, and a running clock.
/// </summary>
/// <remarks>
/// The clock is the point. A still picture cannot tell you whether frames are arriving, whether they are
/// arriving in order, or whether the panel repainted at all — a second hand can. The moving shape does the
/// same job for smoothness, and both are cheap enough to redraw from scratch every frame.
/// </remarks>
public static class SyntheticDesktop
{
    /// <summary>Renders one frame as top-down BGRA8888.</summary>
    public static byte[] Render(int width, int height, double elapsedSeconds, DateTimeOffset now)
    {
        var pixels = new byte[width * height * 4];

        // Ground: a violet → indigo vertical wash, so the panel's letterboxing is obvious against it.
        for (var y = 0; y < height; y++)
        {
            var t = height <= 1 ? 0.0 : (double)y / (height - 1);
            var r = (byte)(28 + (t * 24));
            var g = (byte)(18 + (t * 14));
            var b = (byte)(48 + (t * 46));
            for (var x = 0; x < width; x++)
            {
                Set(pixels, width, x, y, r, g, b);
            }
        }

        // A window, so the frame reads as a desktop rather than a test card.
        var windowX = width / 10;
        var windowY = height / 6;
        var windowW = width * 3 / 5;
        var windowH = height / 2;
        FillRect(pixels, width, height, windowX, windowY, windowW, windowH, 245, 245, 250);
        FillRect(pixels, width, height, windowX, windowY, windowW, Math.Max(6, height / 28), 92, 60, 200);

        // Three lines of "text" in the window, as bars — enough to look like content at any scale.
        var lineHeight = Math.Max(2, height / 60);
        for (var line = 0; line < 5; line++)
        {
            var lineY = windowY + (height / 22) + (line * lineHeight * 3);
            var lineW = (int)(windowW * 0.82 * (line % 2 == 0 ? 1.0 : 0.6));
            FillRect(pixels, width, height, windowX + (windowW / 20), lineY, lineW, lineHeight, 176, 176, 190);
        }

        // The moving shape: a Lissajous path so it never simply loops left-to-right.
        var radius = Math.Max(4, height / 16);
        var centreX = (int)((width / 2.0) + (Math.Sin(elapsedSeconds * 0.9) * (width / 2.6)));
        var centreY = (int)((height * 0.68) + (Math.Sin(elapsedSeconds * 1.4) * (height / 6.0)));
        FillCircle(pixels, width, height, centreX, centreY, radius, 236, 92, 132);

        // The clock, bottom-left, as HH:MM:SS in a 5×7 bitmap font.
        var scale = Math.Max(1, height / 90);
        DrawText(
            pixels,
            width,
            height,
            $"{now.Hour:00}:{now.Minute:00}:{now.Second:00}",
            windowX,
            height - (12 * scale),
            scale,
            250,
            250,
            255);

        return pixels;
    }

    private static void Set(byte[] pixels, int width, int x, int y, byte r, byte g, byte b)
    {
        var offset = ((y * width) + x) * 4;
        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = 255;
    }

    private static void FillRect(byte[] pixels, int width, int height, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        var x1 = Math.Min(width, x + w);
        var y1 = Math.Min(height, y + h);
        for (var py = Math.Max(0, y); py < y1; py++)
        {
            for (var px = Math.Max(0, x); px < x1; px++)
            {
                Set(pixels, width, px, py, r, g, b);
            }
        }
    }

    private static void FillCircle(byte[] pixels, int width, int height, int cx, int cy, int radius, byte r, byte g, byte b)
    {
        for (var py = Math.Max(0, cy - radius); py < Math.Min(height, cy + radius + 1); py++)
        {
            for (var px = Math.Max(0, cx - radius); px < Math.Min(width, cx + radius + 1); px++)
            {
                var dx = px - cx;
                var dy = py - cy;
                if ((dx * dx) + (dy * dy) <= radius * radius)
                {
                    Set(pixels, width, px, py, r, g, b);
                }
            }
        }
    }

    // A 5×7 font covering exactly what the clock needs: ten digits and a colon. Each string is one row,
    // '#' being an inked pixel — legible at a glance in source, which matters more here than compactness.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = ["#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ['6'] = ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
        [':'] = [".....", "..#..", "..#..", ".....", "..#..", "..#..", "....."],
    };

    private static void DrawText(byte[] pixels, int width, int height, string text, int x, int y, int scale, byte r, byte g, byte b)
    {
        var cursor = x;
        foreach (var character in text)
        {
            if (Glyphs.TryGetValue(character, out var rows))
            {
                for (var row = 0; row < rows.Length; row++)
                {
                    for (var column = 0; column < rows[row].Length; column++)
                    {
                        if (rows[row][column] == '#')
                        {
                            FillRect(pixels, width, height, cursor + (column * scale), y + (row * scale), scale, scale, r, g, b);
                        }
                    }
                }
            }

            cursor += 6 * scale;
        }
    }
}
