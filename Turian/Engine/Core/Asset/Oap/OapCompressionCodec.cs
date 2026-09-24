namespace Turian.Engine.Core;

/// <summary>
/// Compression helpers for OAP asset blobs. Only <see cref="OapCompression.Store"/>
/// and <see cref="OapCompression.Deflate"/> (raw RFC&#160;1951) are implemented, which is
/// the conformance baseline; <see cref="OapCompression.Zstd"/> is rejected.
/// </summary>
public static class OapCompressionCodec
{
    /// <summary>Inputs below this size are never deflated — they cannot meaningfully shrink.</summary>
    const int minCompressibleSize = 64;

    /// <summary>Bytes of the input sampled when probing compressibility.</summary>
    const int probeSize = 4096;

    /// <summary>The sampled prefix must shrink by at least this many bytes to warrant a full attempt.</summary>
    const int probeThreshold = 32;

    /// <summary>Compresses <paramref name="data"/> with <paramref name="codec"/>.</summary>
    /// <param name="codec">The codec to apply.</param>
    /// <param name="data">The plaintext bytes.</param>
    /// <returns>The compressed (or, for <see cref="OapCompression.Store"/>, copied) bytes.</returns>
    /// <exception cref="OapUnsupportedCompressionException">The codec cannot be encoded.</exception>
    public static byte[] Compress(OapCompression codec, ReadOnlySpan<byte> data) => codec switch
    {
        OapCompression.Store => [.. data],
        OapCompression.Deflate => Deflate(data),
        _ => throw new OapUnsupportedCompressionException(codec)
    };

    /// <summary>
    /// Decompresses <paramref name="data"/> with <paramref name="codec"/>, producing
    /// exactly <paramref name="uncompressedSize"/> bytes.
    /// </summary>
    /// <param name="codec">The codec the blob was written with.</param>
    /// <param name="data">The stored (compressed) bytes.</param>
    /// <param name="uncompressedSize">The expected plaintext length.</param>
    /// <returns>The plaintext bytes.</returns>
    /// <exception cref="OapUnsupportedCompressionException">The codec cannot be decoded.</exception>
    /// <exception cref="OapCorruptDataException">The output length did not match, or the stream was malformed.</exception>
    public static byte[] Decompress(OapCompression codec, ReadOnlySpan<byte> data, long uncompressedSize)
    {
        switch (codec)
        {
            case OapCompression.Store:
                if (data.Length != uncompressedSize)
                {
                    throw new OapCorruptDataException("Stored OAP blob length does not match its index entry.");
                }

                return [.. data];

            case OapCompression.Deflate:
                return Inflate(data, checked((int)uncompressedSize));

            default:
                throw new OapUnsupportedCompressionException(codec);
        }
    }

    /// <summary>
    /// Picks the smaller of <see cref="OapCompression.Store"/> and
    /// <see cref="OapCompression.Deflate"/> for <paramref name="data"/>, never inflating a
    /// blob. A quick probe on the first 4&#160;KB avoids running full DEFLATE on
    /// incompressible data (random textures, already-compressed or encrypted payloads).
    /// </summary>
    /// <param name="data">The plaintext bytes.</param>
    /// <returns>The chosen codec and the bytes to store.</returns>
    public static (OapCompression Codec, byte[] Stored) Best(ReadOnlySpan<byte> data)
    {
        if (data.Length < minCompressibleSize)
        {
            return (OapCompression.Store, [.. data]);
        }

        var sampleLength = Math.Min(data.Length, probeSize);
        var packedSample = Deflate(data[..sampleLength]);
        if (packedSample.Length >= sampleLength - probeThreshold)
        {
            return (OapCompression.Store, [.. data]);
        }

        var packed = Deflate(data);
        return packed.Length < data.Length
            ? (OapCompression.Deflate, packed)
            : (OapCompression.Store, data.ToArray());
    }

    static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    static byte[] Inflate(ReadOnlySpan<byte> data, int expectedSize)
    {
        var result = new byte[expectedSize];
        try
        {
            using var input = new MemoryStream([.. data], writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);

            var total = 0;
            while (total < expectedSize)
            {
                var read = deflate.Read(result, total, expectedSize - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total != expectedSize || deflate.ReadByte() != -1)
            {
                throw new OapCorruptDataException("Inflated OAP blob length does not match its index entry.");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new OapCorruptDataException("OAP blob DEFLATE stream is malformed.", ex);
        }

        return result;
    }
}
