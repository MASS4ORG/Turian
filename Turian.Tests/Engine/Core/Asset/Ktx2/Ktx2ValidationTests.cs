namespace Turian.Tests;

/// <summary>Tests that the KTX2 reader and writer reject what they cannot represent.</summary>
public class Ktx2ValidationTests
{
    static byte[] Valid()
    {
        using var stream = new MemoryStream();
        Ktx2Writer.Write(stream, Ktx2RoundTripTests.BuildTexture(Ktx2VkFormat.Bc1RgbUnormBlock, 8, 8, levelCount: 4));
        return stream.ToArray();
    }

    static byte[] WithU32(int offset, uint value)
    {
        var bytes = Valid();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        return bytes;
    }

    /// <summary>Only data opening with the identifier is a container.</summary>
    [Fact]
    public void IsKtx2_RecognisesOnlyTheIdentifier()
    {
        Assert.True(Ktx2Reader.IsKtx2(Valid()));
        Assert.False(Ktx2Reader.IsKtx2(DdsFixture.Build("DXT1", 8, 8, 4, 8)));
        Assert.False(Ktx2Reader.IsKtx2([]));
        Assert.Throws<InvalidDataException>(() => Ktx2Reader.Read(new byte[100]));
    }

    /// <summary>A container cut inside its header, level index or level data is rejected.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(-8)]
    public void Read_Truncated_Throws(int length)
    {
        var bytes = Valid();
        var keep = length > 0 ? length : bytes.Length + length;

        Assert.Throws<InvalidDataException>(() => Ktx2Reader.Read(bytes.AsMemory(0, keep)));
    }

    /// <summary>Supercompressed, Basis, array, cube and 3D containers are not cooked payloads.</summary>
    [Theory]
    [InlineData(44, 2u)]
    [InlineData(12, 0u)]
    [InlineData(32, 2u)]
    [InlineData(36, 6u)]
    [InlineData(28, 4u)]
    public void Read_UnsupportedShape_Throws(int offset, uint value)
    {
        Assert.Throws<InvalidDataException>(() => Ktx2Reader.Read(WithU32(offset, value)));
    }

    /// <summary>A key/value entry longer than its section, or without a key, is rejected.</summary>
    [Fact]
    public void Read_MalformedKeyValues_Throws()
    {
        var bytes = Valid();
        var keyValueOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56));

        var tooLong = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooLong.AsSpan(keyValueOffset), 4096);
        Assert.Throws<InvalidDataException>(() => Ktx2Reader.Read(tooLong));

        var noKey = (byte[])bytes.Clone();
        noKey[keyValueOffset + 4] = 0;
        Assert.Throws<InvalidDataException>(() => Ktx2Reader.Read(noKey));
    }

    /// <summary>A content source that ends early is reported, not read as zeros.</summary>
    [Fact]
    public void Read_ContentSourceEndingEarly_Throws()
    {
        using var source = new EmptySource();

        Assert.Throws<InvalidDataException>(() => Ktx2Reader.Read(source));
    }

    sealed class EmptySource : IContentSource
    {
        public ContentIdentity Identity => new("empty", "0");

        public long Length => 200;

        public int ReadAt(long offset, Span<byte> buffer) => 0;

        public Stream OpenRead(long offset, long length) => Stream.Null;

        public void Dispose()
        {
        }
    }

    /// <summary>The writer needs a level and every level sized for its format and extent.</summary>
    [Fact]
    public void Write_RejectsMissingOrMissizedLevels()
    {
        using var stream = new MemoryStream();
        var empty = new Ktx2Texture { Format = Ktx2VkFormat.Bc7UnormBlock, Width = 4, Height = 4, Levels = [] };
        var wrong = empty with { Levels = [new byte[15]] };

        Assert.Throws<ArgumentException>(() => Ktx2Writer.Write(stream, empty));
        Assert.Throws<ArgumentException>(() => Ktx2Writer.Write(stream, wrong));
    }

    /// <summary>Keys must be non-empty and free of NUL, since NUL ends a key on disk.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Turian:\0bad")]
    public void Write_RejectsInvalidKeys(string key)
    {
        using var stream = new MemoryStream();
        var texture = Ktx2RoundTripTests.BuildTexture(Ktx2VkFormat.Bc7UnormBlock, 4, 4, levelCount: 1) with
        {
            KeyValues = new Dictionary<string, ReadOnlyMemory<byte>> { [key] = new byte[] { 1 } },
        };

        Assert.Throws<ArgumentException>(() => Ktx2Writer.Write(stream, texture));
    }

    /// <summary>A format without a descriptor cannot be written, and reports no color space.</summary>
    [Fact]
    public void Write_FormatWithoutDescriptor_Throws()
    {
        using var stream = new MemoryStream();
        var texture = new Ktx2Texture { Format = (Ktx2VkFormat)1000, Width = 1, Height = 1, Levels = [new byte[4]] };

        Assert.False(texture.IsSrgb);
        Assert.Throws<NotSupportedException>(() => Ktx2Writer.Write(stream, texture));
        Assert.Throws<NotSupportedException>(() => Ktx2DataFormat.Build(Ktx2VkFormat.Undefined));
    }
}
