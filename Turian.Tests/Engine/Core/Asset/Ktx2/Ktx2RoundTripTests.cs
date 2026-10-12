namespace Turian.Tests;

/// <summary>Round-trip and layout tests for the KTX2 writer and reader.</summary>
public class Ktx2RoundTripTests
{
    /// <summary>Every format with a descriptor, the ones the importer bakes and passes through.</summary>
    public static TheoryData<Ktx2VkFormat> Formats() =>
        [.. Enum.GetValues<Ktx2VkFormat>().Where(Ktx2DataFormat.IsSupported)];

    /// <summary>Builds a full mip chain with distinct bytes per level so a mix-up shows.</summary>
    internal static Ktx2Texture BuildTexture(Ktx2VkFormat format, uint width, uint height, int levelCount)
    {
        var levels = new ReadOnlyMemory<byte>[levelCount];
        for (var level = 0; level < levelCount; level++)
        {
            var bytes = new byte[Ktx2DataFormat.ImageSize(format, width >> level, height >> level)];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)((level * 31) + index);
            levels[level] = bytes;
        }

        return new Ktx2Texture { Format = format, Width = width, Height = height, Levels = levels };
    }

    static byte[] Write(Ktx2Texture texture)
    {
        using var stream = new MemoryStream();
        Ktx2Writer.Write(stream, texture);
        return stream.ToArray();
    }

    /// <summary>Format, extent, every level's bytes and the color space survive a write and read.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void WriteThenRead_PreservesEveryField(Ktx2VkFormat format)
    {
        var texture = BuildTexture(format, 16, 8, levelCount: 5);

        var read = Ktx2Reader.Read(Write(texture));

        Assert.Equal(format, read.Format);
        Assert.Equal(16u, read.Width);
        Assert.Equal(8u, read.Height);
        Assert.Equal(texture.Levels.Count, read.Levels.Count);
        for (var level = 0; level < texture.Levels.Count; level++)
        {
            Assert.Equal(texture.Levels[level].ToArray(), read.Levels[level].ToArray());
        }

        Assert.Equal(Ktx2DataFormat.Build(format), read.DataFormatDescriptor.ToArray());
        Assert.Equal(texture.IsSrgb, read.IsSrgb);
        Assert.Equal(Ktx2Writer.WriterName, read.GetText(Ktx2Texture.WriterKey));
    }

    /// <summary>
    /// Levels are stored smallest first, each on a multiple of the block size and four, and the index records
    /// uncompressed lengths equal to stored ones, as <c>ktx validate</c> requires.
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void Write_LaysOutLevelsSmallestFirstAndAligned(Ktx2VkFormat format)
    {
        var bytes = Write(BuildTexture(format, 16, 16, levelCount: 5));
        var alignment = Math.Max(4u, Ktx2DataFormat.BlockShape(format).Bytes);

        var previous = ulong.MaxValue;
        for (var level = 0; level < 5; level++)
        {
            var entry = bytes.AsSpan(Ktx2Reader.HeaderLength + (level * Ktx2Reader.LevelEntryLength));
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            Assert.Equal(0ul, offset % alignment);
            Assert.True(offset < previous);
            Assert.Equal(BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]));
            previous = offset;
        }
    }

    /// <summary>The header records a 2D texture: depth and layer count zero, one face, no supercompression.</summary>
    [Fact]
    public void Write_RecordsA2DHeader()
    {
        var bytes = Write(BuildTexture(Ktx2VkFormat.R8G8B8A8Srgb, 4, 2, levelCount: 1));

        Assert.True(Ktx2Reader.IsKtx2(bytes));
        Assert.Equal(43u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44)));
    }

    /// <summary>Custom entries round-trip, sorted by key, and an explicit writer entry replaces the default.</summary>
    [Fact]
    public void WriteThenRead_PreservesKeyValuesSortedByKey()
    {
        var texture = BuildTexture(Ktx2VkFormat.Bc7UnormBlock, 4, 4, levelCount: 1) with
        {
            KeyValues = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["Turian:b"] = new byte[] { 1, 2, 3 },
                ["Turian:a"] = Ktx2Texture.TextValue("hello"),
                [Ktx2Texture.WriterKey] = Ktx2Texture.TextValue("custom"),
            },
        };

        var bytes = Write(texture);
        var read = Ktx2Reader.Read(bytes);

        Assert.Equal("hello", read.GetText("Turian:a"));
        Assert.Equal(new byte[] { 1, 2, 3 }, read.KeyValues["Turian:b"].ToArray());
        Assert.Equal("custom", read.GetText(Ktx2Texture.WriterKey));
        Assert.Null(read.GetText("missing"));

        var text = Encoding.ASCII.GetString(bytes);
        var writer = text.IndexOf("KTXwriter", StringComparison.Ordinal);
        var first = text.IndexOf("Turian:a", StringComparison.Ordinal);
        Assert.True(writer < first);
        Assert.True(first < text.IndexOf("Turian:b", StringComparison.Ordinal));
    }

    /// <summary>Stream, file and content-source reads see the same container as a memory read.</summary>
    [Fact]
    public void Read_FromStreamFileAndContentSource_Agree()
    {
        var bytes = Write(BuildTexture(Ktx2VkFormat.Bc1RgbaSrgbBlock, 8, 8, levelCount: 4));
        var path = Path.Combine(Path.GetTempPath(), $"ktx2-{Guid.NewGuid():N}{Ktx2Reader.FileExtension}");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var stream = new MemoryStream(bytes);
            using var source = FileContentSource.Open(path);

            foreach (var read in new[] { Ktx2Reader.Read(stream), Ktx2Reader.Load(path), Ktx2Reader.Read(source) })
            {
                Assert.Equal(Ktx2VkFormat.Bc1RgbaSrgbBlock, read.Format);
                Assert.Equal(4, read.Levels.Count);
                Assert.True(read.IsSrgb);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Save writes the same bytes as a stream write.</summary>
    [Fact]
    public void Save_WritesTheContainerToDisk()
    {
        var texture = BuildTexture(Ktx2VkFormat.Bc4SnormBlock, 8, 4, levelCount: 2);
        var path = Path.Combine(Path.GetTempPath(), $"ktx2-{Guid.NewGuid():N}{Ktx2Reader.FileExtension}");
        try
        {
            Ktx2Writer.Save(path, texture);

            Assert.Equal(Write(texture), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The engine casts the container's format to Vulkan's, so every value must match its namesake.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void VkFormat_MatchesTheVulkanEnumValue(Ktx2VkFormat format)
    {
        Assert.True(Enum.TryParse<Format>(format.ToString(), ignoreCase: true, out var vulkan));
        Assert.Equal((uint)format, (uint)vulkan);
    }
}
