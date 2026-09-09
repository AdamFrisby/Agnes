namespace Agnes.Client.Simulation;

/// <summary>
/// A minimal baseline JPEG encoder, here so the simulated host can produce a genuinely animated screen.
/// </summary>
/// <remarks>
/// <para>
/// The display channel carries JPEG, so an offline simulation of it has to make JPEGs. The simulation
/// project deliberately has no image library — it is referenced by tests, tools and every head, and adding
/// SkiaSharp to it would push a native dependency into all of them for the sake of a fake screen. A hundred
/// and fifty lines of well-trodden Annex K is the cheaper trade.
/// </para>
/// <para>
/// Baseline sequential, 4:4:4, the spec's example quantisation and Huffman tables scaled by a quality knob.
/// No subsampling and no progressive mode: this needs to be obviously correct, not small.
/// </para>
/// </remarks>
public static class JpegEncoder
{
    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly int[] LuminanceQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly int[] ChrominanceQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] DcLuminanceBits = [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcLuminanceValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] DcChrominanceBits = [0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrominanceValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLuminanceBits = [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];

    private static readonly byte[] AcLuminanceValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16,
        0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4,
        0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea,
        0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    private static readonly byte[] AcChrominanceBits = [0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static readonly byte[] AcChrominanceValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34,
        0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2,
        0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9,
        0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    // cos((2x+1) * u * pi / 16), the only trigonometry the forward DCT needs.
    private static readonly double[] Cosines = BuildCosines();

    private static double[] BuildCosines()
    {
        var table = new double[64];
        for (var u = 0; u < 8; u++)
        {
            for (var x = 0; x < 8; x++)
            {
                table[(u * 8) + x] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
            }
        }

        return table;
    }

    /// <summary>
    /// Encodes a top-down 32-bit BGRA buffer (the layout every head's bitmap uses) as a baseline JPEG.
    /// </summary>
    /// <param name="quality">1–100; 75 is the usual sane default.</param>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int quality = 75)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than the stated geometry.", nameof(bgra));
        }

        var luma = ScaleQuant(LuminanceQuant, quality);
        var chroma = ScaleQuant(ChrominanceQuant, quality);

        var writer = new JpegWriter();
        writer.WriteHeader(width, height, luma, chroma);
        WriteScan(writer, bgra, width, height, luma, chroma);
        writer.WriteMarker(0xD9); // EOI
        return writer.ToArray();
    }

    /// <summary>The spec's quality scaling: below 50 the table is multiplied, above it is divided.</summary>
    private static int[] ScaleQuant(int[] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var scaled = new int[64];
        for (var i = 0; i < 64; i++)
        {
            scaled[i] = Math.Clamp((table[i] * scale + 50) / 100, 1, 255);
        }

        return scaled;
    }

    private static void WriteScan(JpegWriter writer, ReadOnlySpan<byte> bgra, int width, int height, int[] luma, int[] chroma)
    {
        var dcLuma = HuffmanTable.Build(DcLuminanceBits, DcLuminanceValues);
        var acLuma = HuffmanTable.Build(AcLuminanceBits, AcLuminanceValues);
        var dcChroma = HuffmanTable.Build(DcChrominanceBits, DcChrominanceValues);
        var acChroma = HuffmanTable.Build(AcChrominanceBits, AcChrominanceValues);

        Span<double> y = stackalloc double[64];
        Span<double> cb = stackalloc double[64];
        Span<double> cr = stackalloc double[64];
        Span<double> coefficients = stackalloc double[64];
        Span<int> quantized = stackalloc int[64];

        int previousY = 0, previousCb = 0, previousCr = 0;

        for (var blockY = 0; blockY < height; blockY += 8)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                // Edge blocks replicate the last real pixel rather than padding with black, which would put a
                // dark fringe down the right and bottom of any display whose size isn't a multiple of eight.
                for (var row = 0; row < 8; row++)
                {
                    var sourceY = Math.Min(blockY + row, height - 1);
                    for (var column = 0; column < 8; column++)
                    {
                        var sourceX = Math.Min(blockX + column, width - 1);
                        var offset = ((sourceY * width) + sourceX) * 4;
                        double b = bgra[offset];
                        double g = bgra[offset + 1];
                        double r = bgra[offset + 2];
                        var index = (row * 8) + column;
                        y[index] = (0.299 * r) + (0.587 * g) + (0.114 * b) - 128.0;
                        cb[index] = (-0.168736 * r) - (0.331264 * g) + (0.5 * b);
                        cr[index] = (0.5 * r) - (0.418688 * g) - (0.081312 * b);
                    }
                }

                previousY = EncodeBlock(writer, y, luma, dcLuma, acLuma, previousY, coefficients, quantized);
                previousCb = EncodeBlock(writer, cb, chroma, dcChroma, acChroma, previousCb, coefficients, quantized);
                previousCr = EncodeBlock(writer, cr, chroma, dcChroma, acChroma, previousCr, coefficients, quantized);
            }
        }

        writer.FlushBits();
    }

    private static int EncodeBlock(
        JpegWriter writer,
        ReadOnlySpan<double> samples,
        int[] quant,
        HuffmanTable dc,
        HuffmanTable ac,
        int previousDc,
        Span<double> coefficients,
        Span<int> quantized)
    {
        ForwardDct(samples, coefficients);
        for (var i = 0; i < 64; i++)
        {
            quantized[i] = (int)Math.Round(coefficients[ZigZag[i]] / quant[i]);
        }

        var diff = quantized[0] - previousDc;
        var (size, bits) = Magnitude(diff);
        writer.WriteCode(dc, size);
        if (size > 0)
        {
            writer.WriteBits(bits, size);
        }

        // The last AC coefficient that isn't zero, so the rest of the block can collapse into one EOB. Index 0
        // is the DC term, already written above, so the walk stops before it rather than at it.
        var lastNonZero = 0;
        var scan = 63;
        while (scan > 0)
        {
            if (quantized[scan] != 0)
            {
                lastNonZero = scan;
                break;
            }

            scan--;
        }

        var run = 0;
        for (var i = 1; i <= lastNonZero; i++)
        {
            if (quantized[i] == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                writer.WriteCode(ac, 0xF0); // ZRL: sixteen zeroes
                run -= 16;
            }

            var (acSize, acBits) = Magnitude(quantized[i]);
            writer.WriteCode(ac, (run << 4) | acSize);
            writer.WriteBits(acBits, acSize);
            run = 0;
        }

        if (lastNonZero < 63)
        {
            writer.WriteCode(ac, 0x00); // EOB
        }

        return quantized[0];
    }

    private static void ForwardDct(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> rows = stackalloc double[64];
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var x = 0; x < 8; x++)
                {
                    sum += input[(y * 8) + x] * Cosines[(u * 8) + x];
                }

                rows[(y * 8) + u] = sum * (u == 0 ? 0.353553390593273762 : 0.5);
            }
        }

        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                var sum = 0.0;
                for (var y = 0; y < 8; y++)
                {
                    sum += rows[(y * 8) + u] * Cosines[(v * 8) + y];
                }

                output[(v * 8) + u] = sum * (v == 0 ? 0.353553390593273762 : 0.5);
            }
        }
    }

    /// <summary>The magnitude category of a coefficient and the bits that encode it (negatives biased).</summary>
    private static (int Size, int Bits) Magnitude(int value)
    {
        if (value == 0)
        {
            return (0, 0);
        }

        var magnitude = Math.Abs(value);
        var size = 0;
        while (magnitude > 0)
        {
            size++;
            magnitude >>= 1;
        }

        var bits = value > 0 ? value : value + (1 << size) - 1;
        return (size, bits);
    }

    /// <summary>A canonical Huffman table: the code and length for each symbol.</summary>
    private sealed record HuffmanTable(int[] Codes, int[] Lengths)
    {
        public static HuffmanTable Build(byte[] bits, byte[] values)
        {
            var codes = new int[256];
            var lengths = new int[256];
            var code = 0;
            var k = 0;
            for (var length = 1; length <= 16; length++)
            {
                for (var i = 0; i < bits[length]; i++)
                {
                    codes[values[k]] = code;
                    lengths[values[k]] = length;
                    code++;
                    k++;
                }

                code <<= 1;
            }

            return new HuffmanTable(codes, lengths);
        }
    }

    /// <summary>Marker and bit-level output. Entropy bytes are stuffed per the spec (0xFF → 0xFF 0x00).</summary>
    private sealed class JpegWriter
    {
        private readonly MemoryStream _stream = new();
        private int _bitBuffer;
        private int _bitCount;

        public byte[] ToArray() => _stream.ToArray();

        public void WriteMarker(byte marker)
        {
            _stream.WriteByte(0xFF);
            _stream.WriteByte(marker);
        }

        public void WriteHeader(int width, int height, int[] luma, int[] chroma)
        {
            WriteMarker(0xD8); // SOI

            // APP0 / JFIF
            WriteMarker(0xE0);
            WriteSegmentLength(16);
            _stream.Write("JFIF\0"u8);
            _stream.WriteByte(1);
            _stream.WriteByte(1);
            _stream.WriteByte(0);       // no density units
            WriteUInt16(1);
            WriteUInt16(1);
            _stream.WriteByte(0);
            _stream.WriteByte(0);

            WriteQuantTable(0, luma);
            WriteQuantTable(1, chroma);

            // SOF0: baseline, 8-bit, three components, no subsampling.
            WriteMarker(0xC0);
            WriteSegmentLength(17);
            _stream.WriteByte(8);
            WriteUInt16(height);
            WriteUInt16(width);
            _stream.WriteByte(3);
            foreach (var (id, table) in new[] { (1, 0), (2, 1), (3, 1) })
            {
                _stream.WriteByte((byte)id);
                _stream.WriteByte(0x11); // 1×1 sampling
                _stream.WriteByte((byte)table);
            }

            WriteHuffmanTable(0x00, DcLuminanceBits, DcLuminanceValues);
            WriteHuffmanTable(0x10, AcLuminanceBits, AcLuminanceValues);
            WriteHuffmanTable(0x01, DcChrominanceBits, DcChrominanceValues);
            WriteHuffmanTable(0x11, AcChrominanceBits, AcChrominanceValues);

            // SOS
            WriteMarker(0xDA);
            WriteSegmentLength(12);
            _stream.WriteByte(3);
            _stream.WriteByte(1);
            _stream.WriteByte(0x00); // luma: DC 0, AC 0
            _stream.WriteByte(2);
            _stream.WriteByte(0x11); // chroma: DC 1, AC 1
            _stream.WriteByte(3);
            _stream.WriteByte(0x11);
            _stream.WriteByte(0);
            _stream.WriteByte(63);
            _stream.WriteByte(0);
        }

        private void WriteQuantTable(byte id, int[] table)
        {
            WriteMarker(0xDB);
            WriteSegmentLength(67);
            _stream.WriteByte(id); // 8-bit precision, table id
            for (var i = 0; i < 64; i++)
            {
                _stream.WriteByte((byte)table[i]);
            }
        }

        private void WriteHuffmanTable(byte id, byte[] bits, byte[] values)
        {
            WriteMarker(0xC4);
            WriteSegmentLength(3 + 16 + values.Length);
            _stream.WriteByte(id);
            for (var i = 1; i <= 16; i++)
            {
                _stream.WriteByte(bits[i]);
            }

            _stream.Write(values);
        }

        private void WriteSegmentLength(int length) => WriteUInt16(length);

        private void WriteUInt16(int value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)(value & 0xFF));
        }

        public void WriteCode(HuffmanTable table, int symbol) => WriteBits(table.Codes[symbol], table.Lengths[symbol]);

        public void WriteBits(int bits, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _bitBuffer = (_bitBuffer << 1) | ((bits >> i) & 1);
                _bitCount++;
                if (_bitCount != 8)
                {
                    continue;
                }

                EmitEntropyByte((byte)_bitBuffer);
                _bitBuffer = 0;
                _bitCount = 0;
            }
        }

        public void FlushBits()
        {
            while (_bitCount > 0)
            {
                // Pad with ones, so the tail can never be mistaken for a marker prefix.
                WriteBits(1, 1);
            }
        }

        private void EmitEntropyByte(byte value)
        {
            _stream.WriteByte(value);
            if (value == 0xFF)
            {
                _stream.WriteByte(0x00);
            }
        }
    }
}
