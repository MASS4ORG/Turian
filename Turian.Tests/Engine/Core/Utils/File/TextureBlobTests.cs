namespace Turian.Tests;

/// <summary>Tests for the <c>.amtex</c> container reader and writer.</summary>
public class TextureBlobTests
{
    static TextureBlobContent BuildContent(Format format, uint width, uint height, int levelCount)
    {
        var levels = new List<ReadOnlyMemory<byte>>(levelCount);
        for (var level = 0; level < levelCount; level++)
        {
            var (levelWidth, levelHeight) = TextureFormats.LevelExtent(width, height, level);
            var size = (int)TextureFormats.LevelSizeBytes(format, levelWidth, levelHeight);
            var bytes = new byte[size];
            Array.Fill(bytes, (byte)(level + 1));
            levels.Add(bytes);
        }

        return new TextureBlobContent(format, width, height, IsSrgb: true, levels);
    }

    /// <summary>A written container reads back with the same format, extent and level bytes.</summary>
    [Fact]
    public void RoundTrip_PreservesFormatExtentAndLevels()
    {
        var content = BuildContent(Format.BC3SrgbBlock, 16, 8, levelCount: 5);

        using var stream = new MemoryStream();
        TextureBlobWriter.Write(stream, content);
        var blob = TextureBlob.Read(stream.ToArray());

        Assert.Equal(content.Format, blob.Format);
        Assert.Equal(content.Width, blob.Width);
        Assert.Equal(content.Height, blob.Height);
        Assert.True(blob.IsSrgb);
        Assert.Equal(content.Levels.Count, blob.Levels.Count);

        for (var level = 0; level < blob.Levels.Count; level++)
        {
            Assert.Equal(content.Levels[level].ToArray(), blob.Levels[level].ToArray());
        }
    }

    /// <summary>A single-level texture round-trips as readily as a full chain.</summary>
    [Fact]
    public void RoundTrip_SingleLevel()
    {
        var content = BuildContent(Format.BC5UnormBlock, 4, 4, levelCount: 1);

        using var stream = new MemoryStream();
        TextureBlobWriter.Write(stream, content);
        var blob = TextureBlob.Read(stream.ToArray());

        Assert.Single(blob.Levels);
        Assert.Equal(content.IsSrgb, blob.IsSrgb);
    }

    /// <summary>The magic sniff distinguishes a container from a source image format.</summary>
    [Fact]
    public void IsTextureBlob_RecognisesOnlyTheContainer()
    {
        using var stream = new MemoryStream();
        TextureBlobWriter.Write(stream, BuildContent(Format.BC1RgbaUnormBlock, 8, 8, levelCount: 4));

        Assert.True(TextureBlob.IsTextureBlob(stream.ToArray()));
        Assert.False(TextureBlob.IsTextureBlob(DdsFixture.Build("DXT1", 8, 8, 4, 8)));
        Assert.False(TextureBlob.IsTextureBlob([]));
    }

    /// <summary>A truncated container is rejected rather than read as garbage.</summary>
    [Fact]
    public void Read_TruncatedContainer_Throws()
    {
        using var stream = new MemoryStream();
        TextureBlobWriter.Write(stream, BuildContent(Format.BC1RgbaUnormBlock, 8, 8, levelCount: 4));
        var bytes = stream.ToArray();

        Assert.Throws<InvalidDataException>(() => TextureBlob.Read(bytes.AsMemory(0, bytes.Length - 16)));
    }

    /// <summary>Writing a container with no levels is a programming error, not a valid file.</summary>
    [Fact]
    public void Write_NoLevels_Throws()
    {
        var empty = new TextureBlobContent(Format.BC1RgbaUnormBlock, 4, 4, IsSrgb: false, []);

        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => TextureBlobWriter.Write(stream, empty));
    }
}
