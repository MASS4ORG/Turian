namespace Turian.Tests;

/// <summary>Tests for the OAP <c>store</c> / <c>deflate</c> codecs and the auto heuristic.</summary>
public class OapCompressionTests
{
    /// <summary>Stored data round-trips to the exact same bytes.</summary>
    [Fact]
    public void Store_RoundTrips()
    {
        var data = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        var stored = OapCompressionCodec.Compress(OapCompression.Store, data);
        var back = OapCompressionCodec.Decompress(OapCompression.Store, stored, data.Length);
        Assert.Equal(data, back);
    }

    /// <summary>Deflated data shrinks and round-trips to the exact same bytes.</summary>
    [Fact]
    public void Deflate_RoundTripsAndShrinks()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ABCABCABCABC", 200)));
        var stored = OapCompressionCodec.Compress(OapCompression.Deflate, data);
        Assert.True(stored.Length < data.Length);

        var back = OapCompressionCodec.Decompress(OapCompression.Deflate, stored, data.Length);
        Assert.Equal(data, back);
    }

    /// <summary>Decompressing to the wrong expected size is reported as corrupt.</summary>
    [Fact]
    public void Deflate_WrongExpectedSize_Throws()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 64)));
        var stored = OapCompressionCodec.Compress(OapCompression.Deflate, data);
        Assert.Throws<OapCorruptDataException>(
            () => OapCompressionCodec.Decompress(OapCompression.Deflate, stored, data.Length + 5));
    }

    /// <summary><see cref="OapCompressionCodec.Best"/> keeps incompressible data stored.</summary>
    [Fact]
    public void Best_KeepsRandomDataStored()
    {
        var random = RandomNumberGenerator.GetBytes(8192);
        var (codec, stored) = OapCompressionCodec.Best(random);
        Assert.Equal(OapCompression.Store, codec);
        Assert.Equal(random, stored);
    }

    /// <summary><see cref="OapCompressionCodec.Best"/> deflates compressible data.</summary>
    [Fact]
    public void Best_DeflatesCompressibleData()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compress me please ", 500)));
        var (codec, stored) = OapCompressionCodec.Best(data);
        Assert.Equal(OapCompression.Deflate, codec);
        Assert.True(stored.Length < data.Length);
    }

    /// <summary>Tiny inputs are always stored.</summary>
    [Fact]
    public void Best_KeepsTinyInputStored()
    {
        var (codec, _) = OapCompressionCodec.Best("x"u8);
        Assert.Equal(OapCompression.Store, codec);
    }

    /// <summary>Zstandard round-trips, and <see cref="OapCompressionCodec.Best"/> picks it when it is smaller.</summary>
    [Fact]
    public void Zstd_RoundTripsAndWinsOnCompressibleData()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("zstandard ", 2000)));

        var (codec, stored) = OapCompressionCodec.Best(data, OapCompression.Zstd);

        Assert.Equal(OapCompression.Zstd, codec);
        Assert.True(stored.Length < data.Length / 10);
        Assert.Equal(data, OapCompressionCodec.Decompress(OapCompression.Zstd, stored, data.Length));
    }

    /// <summary>A frame larger than the 8 MiB window limit still decodes, because the writer caps the window.</summary>
    [Fact]
    public void Zstd_LargeInputStaysWithinTheWindowLimit()
    {
        var data = new byte[12 * 1024 * 1024];
        new Random(7).NextBytes(data.AsSpan(0, 4096));
        for (var i = 4096; i < data.Length; i++) data[i] = data[i % 4096];

        var stored = OapCompressionCodec.Compress(OapCompression.Zstd, data, level: 3);

        Assert.Equal(data, OapCompressionCodec.Decompress(OapCompression.Zstd, stored, data.Length));
    }

    /// <summary>A truncated frame or a wrong expected length is reported as corrupt data.</summary>
    [Fact]
    public void Zstd_CorruptFrameIsReported()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("frame ", 500)));
        var stored = OapCompressionCodec.Compress(OapCompression.Zstd, data);

        Assert.Throws<OapCorruptDataException>(
            () => OapCompressionCodec.Decompress(OapCompression.Zstd, stored, data.Length + 1));
        Assert.Throws<OapCorruptDataException>(
            () => OapCompressionCodec.Decompress(OapCompression.Zstd, stored.AsSpan(0, stored.Length - 4),
                data.Length));
    }

    /// <summary>An unknown codec value is rejected rather than returned undecoded.</summary>
    [Fact]
    public void UnknownCodec_IsRejected()
    {
        Assert.Throws<OapUnsupportedCompressionException>(
            () => OapCompressionCodec.Compress((OapCompression)9, "data"u8));
        Assert.Throws<OapUnsupportedCompressionException>(
            () => OapCompressionCodec.Decompress((OapCompression)9, "data"u8, 4));
    }
}
