using System.Buffers.Binary;
using System.Text.Json;

namespace Agnes.Relay.Protocol;

/// <summary>
/// Length-prefixed JSON framing for <see cref="RelayFrame"/>s: a 4-byte big-endian length, then
/// that many bytes of UTF-8 JSON. Reading consumes <b>exactly</b> one frame and never over-reads,
/// so any bytes that follow the last setup frame remain untouched in the stream — that is what lets
/// the relay hand the raw stream straight to opaque forwarding without losing a single payload byte.
/// </summary>
public static class RelayFrameCodec
{
    /// <summary>Hard cap on a single setup frame — these are tiny; anything larger is abuse.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteFrameAsync(Stream stream, RelayFrame frame, CancellationToken ct = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (json.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"Relay frame too large ({json.Length} bytes).");
        }

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)json.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads exactly one frame. Returns <c>null</c> on a clean end-of-stream (peer closed before
    /// sending another frame). Throws on a malformed/oversized frame or a partial read.
    /// </summary>
    public static async Task<RelayFrame?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] prefix = new byte[4];
        int read = await ReadUpToAsync(stream, prefix, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null; // clean EOF at a frame boundary.
        }

        if (read < 4)
        {
            throw new EndOfStreamException("Truncated relay frame length prefix.");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length is 0 or > MaxFrameBytes)
        {
            throw new InvalidOperationException($"Invalid relay frame length ({length}).");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RelayFrame>(body, Json)
            ?? throw new InvalidOperationException("Relay frame decoded to null.");
    }

    /// <summary>Fills <paramref name="buffer"/> as far as possible; returns bytes read (0 on immediate EOF).</summary>
    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
