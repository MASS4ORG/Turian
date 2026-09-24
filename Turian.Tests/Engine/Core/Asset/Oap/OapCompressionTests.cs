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

    /// <summary>The reserved zstd codec is rejected.</summary>
    [Fact]
    public void Zstd_IsRejected()
    {
        Assert.Throws<OapUnsupportedCompressionException>(
            () => OapCompressionCodec.Compress(OapCompression.Zstd, "data"u8));
    }
}
