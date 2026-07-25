using Agnes.Ui.Core.Qr;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The QR carries a one-time pairing credential off one screen and into a phone camera, so the things
/// that matter are that it encodes the whole payload and keeps the structure a scanner looks for.
/// </summary>
public sealed class QrMatrixTests
{
    /// <summary>A realistic payload: reachable host, 256-bit grant (43 base64url chars), session id.</summary>
    private const string PairingLink =
        "agnes://pair?host=https%3A%2F%2Fstudio.lan%3A5099&grant=" +
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ&session=sim-0001";

    [Fact]
    public void A_pairing_link_encodes()
    {
        var matrix = QrMatrix.Encode(PairingLink);

        // Square, and big enough to be a real QR version rather than a degenerate grid.
        Assert.True(matrix.Size >= 25, $"unexpectedly small: {matrix.Size}");
        Assert.True(matrix.Size % 2 == 1); // QR versions are odd-sized, quiet zone included
    }

    [Fact]
    public void The_finder_patterns_are_where_a_scanner_looks_for_them()
    {
        var matrix = QrMatrix.Encode(PairingLink);

        // QRCoder includes a 4-module quiet zone, so the top-left finder's 7x7 block starts at (4,4).
        // Its outer ring is dark and the ring inside it is light — if that inverts or shifts, no scanner
        // will lock on, and the code would look plausible while being unreadable.
        const int q = 4;
        for (var i = 0; i < 7; i++)
        {
            Assert.True(matrix[q + i, q], "finder top edge should be dark");
            Assert.True(matrix[q, q + i], "finder left edge should be dark");
        }

        Assert.False(matrix[q + 1, q + 1], "the ring inside the finder should be light");
        Assert.True(matrix[q + 3, q + 3], "the finder's centre should be dark");
    }

    [Fact]
    public void A_quiet_zone_surrounds_the_code()
    {
        var matrix = QrMatrix.Encode(PairingLink);

        // Scanners need the margin; cropping it is a classic way to ship a code that looks right and
        // reads badly.
        for (var i = 0; i < matrix.Size; i++)
        {
            Assert.False(matrix[i, 0]);
            Assert.False(matrix[0, i]);
            Assert.False(matrix[i, matrix.Size - 1]);
            Assert.False(matrix[matrix.Size - 1, i]);
        }
    }

    [Fact]
    public void A_longer_payload_needs_a_bigger_grid()
    {
        var small = QrMatrix.Encode("agnes://pair?host=https%3A%2F%2Fx%3A1");
        var large = QrMatrix.Encode(PairingLink + "&extra=" + new string('z', 400));

        Assert.True(large.Size > small.Size);
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        var a = QrMatrix.Encode(PairingLink);
        var b = QrMatrix.Encode(PairingLink);

        for (var y = 0; y < a.Size; y++)
        {
            for (var x = 0; x < a.Size; x++)
            {
                Assert.Equal(a[x, y], b[x, y]);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_encode_is_rejected_rather_than_drawn_blank(string text)
        => Assert.Throws<ArgumentException>(() => QrMatrix.Encode(text));
}
