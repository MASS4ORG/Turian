using ZstdSharp;
using ZstdSharp.Unsafe;

namespace OpenAssetPackage;

/// <summary>
/// Compression helpers for OAP asset blobs: <see cref="OapCompression.Store"/>, raw DEFLATE and Zstandard
/// frames limited to the spec's 8&#160;MiB window. Chunked blobs build on these per block (<see cref="OapChunks"/>).
/// </summary>
public static class OapCompressionCodec
{
    /// <summary>The largest Zstandard window log a frame may use (8&#160;MiB, spec §5.1.1).</summary>
    public const int ZstdMaxWindowLog = 23;

    /// <summary>
    /// The Zstandard level used when a choice leaves the level at 0: a better ratio than DEFLATE at a similar packing
    /// speed. Decoding speed does not depend on the level.
    /// </summary>
    public const int ZstdDefaultLevel = 9;

    /// <summary>Inputs below this size are never compressed — they cannot meaningfully shrink.</summary>
    const int minCompressibleSize = 64;

    /// <summary>Bytes of the input sampled when probing compressibility.</summary>
    const int probeSize = 4096;

    /// <summary>The sampled prefix must shrink by at least this many bytes to warrant a full attempt.</summary>
    const int probeThreshold = 32;

    [ThreadStatic] static Decompressor? zstdDecompressor;

    /// <summary>Compresses <paramref name="data"/> with <paramref name="codec"/>.</summary>
    /// <param name="codec">The codec to apply.</param>
    /// <param name="data">The plaintext bytes.</param>
    /// <param name="level">The codec level, or 0 for the codec's default.</param>
    /// <returns>The compressed (or, for <see cref="OapCompression.Store"/>, copied) bytes.</returns>
    /// <exception cref="OapUnsupportedCompressionException">The codec cannot be encoded.</exception>
    public static byte[] Compress(OapCompression codec, ReadOnlySpan<byte> data, int level = 0) => codec switch
    {
        OapCompression.Store => [.. data],
        OapCompression.Deflate => Deflate(data),
        OapCompression.Zstd => Zstd(data, level),
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
    /// <exception cref="OapCorruptDataException">The output length did not match, or the data was malformed.</exception>
    public static byte[] Decompress(OapCompression codec, ReadOnlySpan<byte> data, long uncompressedSize)
    {
        var result = new byte[checked((int)uncompressedSize)];
        DecompressInto(codec, data, result);
        return result;
    }

    /// <summary>Decompresses <paramref name="data"/> to fill exactly <paramref name="destination"/>.</summary>
    /// <param name="codec">The codec the bytes were written with.</param>
    /// <param name="data">The compressed bytes.</param>
    /// <param name="destination">Receives the plaintext; its length is the expected plaintext length.</param>
    /// <exception cref="OapUnsupportedCompressionException">The codec cannot be decoded.</exception>
    /// <exception cref="OapCorruptDataException">The output length did not match, or the data was malformed.</exception>
    public static void DecompressInto(OapCompression codec, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        switch (codec)
        {
            case OapCompression.Store:
                if (data.Length != destination.Length)
                {
                    throw new OapCorruptDataException("Stored OAP blob length does not match its index entry.");
                }

                data.CopyTo(destination);
                return;

            case OapCompression.Deflate:
                Inflate(data, destination);
                return;

            case OapCompression.Zstd:
                Unzstd(data, destination);
                return;

            default:
                throw new OapUnsupportedCompressionException(codec);
        }
    }

    /// <summary>
    /// Picks the smaller of <see cref="OapCompression.Store"/> and <paramref name="codec"/> for
    /// <paramref name="data"/>, never inflating a blob. A quick probe on the first 4&#160;KB avoids running the
    /// codec on incompressible data (random textures, already-compressed or encrypted payloads).
    /// </summary>
    /// <param name="data">The plaintext bytes.</param>
    /// <param name="codec">The codec to try against storing.</param>
    /// <param name="level">The codec level, or 0 for the codec's default.</param>
    /// <returns>The chosen codec and the bytes to store.</returns>
    public static (OapCompression Codec, byte[] Stored) Best(ReadOnlySpan<byte> data,
        OapCompression codec = OapCompression.Deflate, int level = 0)
    {
        if (codec == OapCompression.Store || data.Length < minCompressibleSize)
        {
            return (OapCompression.Store, [.. data]);
        }

        var sampleLength = Math.Min(data.Length, probeSize);
        var packedSample = Compress(codec, data[..sampleLength], level);
        if (packedSample.Length >= sampleLength - probeThreshold)
        {
            return (OapCompression.Store, [.. data]);
        }

        var packed = Compress(codec, data, level);
        return packed.Length < data.Length
            ? (codec, packed)
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

    static void Inflate(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        try
        {
            using var input = new MemoryStream([.. data], writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);

            var total = 0;
            while (total < destination.Length)
            {
                var read = deflate.Read(destination[total..]);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total != destination.Length || deflate.ReadByte() != -1)
            {
                throw new OapCorruptDataException("Inflated OAP blob length does not match its index entry.");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new OapCorruptDataException("OAP blob DEFLATE stream is malformed.", ex);
        }
    }

    static byte[] Zstd(ReadOnlySpan<byte> data, int level)
    {
        using var compressor = new Compressor(level == 0 ? ZstdDefaultLevel : level);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog,
            Math.Min(ZstdMaxWindowLog, WindowLogFor(data.Length)));
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
        return compressor.Wrap(data).ToArray();
    }

    // The smallest window covering the input, so tiny assets never declare an 8 MiB window to the decoder.
    static int WindowLogFor(int length) =>
        Math.Max(10, 32 - BitOperations.LeadingZeroCount((uint)Math.Max(1, length - 1)));

    static void Unzstd(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var decompressor = zstdDecompressor ??= CreateDecompressor();
        int written;
        try
        {
            written = decompressor.Unwrap(data, destination);
        }
        catch (ZstdException ex)
        {
            throw new OapCorruptDataException("OAP blob Zstandard frame is malformed or exceeds the 8 MiB window.", ex);
        }

        if (written != destination.Length)
        {
            throw new OapCorruptDataException("Zstandard OAP blob length does not match its index entry.");
        }
    }

    static Decompressor CreateDecompressor()
    {
        var decompressor = new Decompressor();
        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, ZstdMaxWindowLog);
        return decompressor;
    }
}
