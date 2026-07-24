using System.Buffers.Binary;

namespace Agnes.Host.Events;

/// <summary>
/// Pure vector helpers for the embeddings memory tier (see .ideas/ops/02-memory-search.md): cosine
/// similarity and a compact little-endian <c>float[]</c>↔blob encoding for the SQLite vector column.
/// Kept BCL-only and side-effect-free so the ranking math is unit-testable without a database or a model.
/// </summary>
internal static class VectorMath
{
    /// <summary>
    /// Cosine similarity of two equal-length vectors, in [-1, 1] (higher = more similar). Returns 0 for a
    /// length mismatch or a zero-magnitude vector rather than throwing or yielding NaN, so a malformed row
    /// can never poison a ranking.
    /// </summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0d;
        }

        double dot = 0d;
        double magA = 0d;
        double magB = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            double x = a[i];
            double y = b[i];
            dot += x * y;
            magA += x * x;
            magB += y * y;
        }

        if (magA <= 0d || magB <= 0d)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    /// <summary>Encodes a vector as little-endian IEEE-754 bytes for storage in a SQLite BLOB column.</summary>
    public static byte[] ToBlob(ReadOnlySpan<float> vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float)), vector[i]);
        }

        return blob;
    }

    /// <summary>Decodes a BLOB written by <see cref="ToBlob"/> back into a vector.</summary>
    public static float[] FromBlob(ReadOnlySpan<byte> blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.Slice(i * sizeof(float)));
        }

        return vector;
    }
}
