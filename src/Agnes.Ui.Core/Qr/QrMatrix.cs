using QRCoder;

namespace Agnes.Ui.Core.Qr;

/// <summary>
/// A QR code as a plain grid of dark/light modules, ready for any UI to draw.
///
/// Deliberately not an image: every head here is a vector renderer, so handing back a bitmap would mean
/// encoding a PNG only to decode it again, and would fix the colours at generation time. A matrix draws
/// crisply at any size and in whatever brand colours the surrounding theme is using.
///
/// The quiet zone QRCoder includes is kept — scanners need it, and cropping it is a classic way to make
/// a code that looks right and reads badly.
/// </summary>
public sealed class QrMatrix
{
    private readonly bool[,] _modules;

    private QrMatrix(bool[,] modules, int size)
    {
        _modules = modules;
        Size = size;
    }

    /// <summary>Width and height of the grid, in modules (quiet zone included).</summary>
    public int Size { get; }

    /// <summary>Whether the module at this position is dark.</summary>
    public bool this[int x, int y] => _modules[x, y];

    /// <summary>
    /// Encodes text as a QR grid.
    ///
    /// Error-correction level Q (~25% recoverable) rather than the usual L: these codes get scanned off
    /// a glossy laptop screen at an angle, and the payload is small enough that the extra redundancy
    /// costs a version or two of size and nothing else.
    /// </summary>
    public static QrMatrix Encode(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);

        var size = data.ModuleMatrix.Count;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            var row = data.ModuleMatrix[y];
            for (var x = 0; x < size; x++)
            {
                modules[x, y] = row[x];
            }
        }

        return new QrMatrix(modules, size);
    }
}
