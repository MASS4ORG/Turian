namespace Turian.Tests;

/// <summary>Tests for the DDS header parser and its mip-offset arithmetic.</summary>
public class DdsReaderTests
{
    /// <summary>DXT1 maps to BC1 in whichever color space the asset declares.</summary>
    [Theory]
    [InlineData(true, Format.BC1RgbaSrgbBlock)]
    [InlineData(false, Format.BC1RgbaUnormBlock)]
    public void Read_Dxt1_MapsToBc1InRequestedColorSpace(bool isSrgb, Format expected)
    {
        var file = DdsFixture.Build("DXT1", 8, 8, mipMapCount: 4, blockBytes: 8);

        var image = DdsReader.Read(file, isSrgb);

        Assert.Equal(expected, image.Format);
    }

    /// <summary>DXT5 maps to BC3 in whichever color space the asset declares.</summary>
    [Theory]
    [InlineData(true, Format.BC3SrgbBlock)]
    [InlineData(false, Format.BC3UnormBlock)]
    public void Read_Dxt5_MapsToBc3InRequestedColorSpace(bool isSrgb, Format expected)
    {
        var file = DdsFixture.Build("DXT5", 8, 8, mipMapCount: 4, blockBytes: 16);

        var image = DdsReader.Read(file, isSrgb);

        Assert.Equal(expected, image.Format);
    }

    /// <summary>
    /// ATI2 is BC5, which stores two linear channels. Asking for sRGB must not produce an sRGB
    /// format — there is no such BC5 variant, and Bistro's normal maps arrive tagged this way
    /// whenever a folder scan reaches them before a material does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_Ati2_IsAlwaysLinearBc5(bool isSrgb)
    {
        var file = DdsFixture.Build("ATI2", 16, 16, mipMapCount: 5, blockBytes: 16);

        var image = DdsReader.Read(file, isSrgb);

        Assert.Equal(Format.BC5UnormBlock, image.Format);
    }

    /// <summary>
    /// A zero mip-map count means one level, not zero. 33 of Bistro's normal maps are written
    /// this way.
    /// </summary>
    [Fact]
    public void Read_ZeroMipMapCount_YieldsOneLevel()
    {
        var file = DdsFixture.Build("ATI2", 16, 16, mipMapCount: 0, blockBytes: 16);

        var image = DdsReader.Read(file, isSrgb: false);

        Assert.Single(image.Levels);
        Assert.Equal(16u, image.Width);
        Assert.Equal(16u, image.Height);
    }

    /// <summary>Each level slices the payload at the offset its block count implies.</summary>
    [Fact]
    public void Read_SlicesEachLevelAtItsBlockAlignedOffset()
    {
        const uint width = 16;
        const uint height = 16;
        const uint blockBytes = 8;
        var file = DdsFixture.Build("DXT1", width, height, mipMapCount: 5, blockBytes);

        var image = DdsReader.Read(file, isSrgb: false);

        Assert.Equal(5, image.Levels.Count);
        for (var level = 0; level < image.Levels.Count; level++)
        {
            Assert.Equal((int)DdsFixture.LevelBytes(width, height, level, blockBytes), image.Levels[level].Length);

            // The fixture stamps every byte of a level with its 1-based index.
            Assert.All(image.Levels[level].ToArray(), value => Assert.Equal((byte)(level + 1), value));
        }
    }

    /// <summary>
    /// Levels below 4×4 still occupy one whole block, so a chain that runs past a
    /// power-of-two boundary keeps its offsets aligned.
    /// </summary>
    [Fact]
    public void Read_LevelsSmallerThanOneBlock_StillOccupyAWholeBlock()
    {
        var file = DdsFixture.Build("DXT5", 4, 4, mipMapCount: 3, blockBytes: 16);

        var image = DdsReader.Read(file, isSrgb: false);

        Assert.Equal(3, image.Levels.Count);
        Assert.All(image.Levels, level => Assert.Equal(16, level.Length));
    }

    /// <summary>A non-power-of-two extent rounds up to whole blocks in both axes.</summary>
    [Fact]
    public void Read_NonPowerOfTwoExtent_RoundsUpToWholeBlocks()
    {
        // 9x5 is 3 blocks wide (ceil(9/4)) and 2 blocks high (ceil(5/4)).
        var file = DdsFixture.Build("DXT1", 9, 5, mipMapCount: 1, blockBytes: 8);

        var image = DdsReader.Read(file, isSrgb: false);

        Assert.Equal(3 * 2 * 8, image.Levels[0].Length);
    }

    /// <summary>A file that is not a DDS is rejected rather than misread.</summary>
    [Fact]
    public void Read_NonDdsPayload_Throws()
    {
        Assert.False(DdsReader.IsDds("not a dds"u8));
        Assert.Throws<InvalidDataException>(() => DdsReader.Read("not a dds"u8.ToArray(), isSrgb: false));
    }

    /// <summary>An unrecognised fourCC names itself in the error, so a bad import is diagnosable.</summary>
    [Fact]
    public void Read_UnsupportedFourCc_NamesItInTheError()
    {
        var file = DdsFixture.Build("YUY2", 8, 8, mipMapCount: 1, blockBytes: 8);

        var error = Assert.Throws<InvalidDataException>(() => DdsReader.Read(file, isSrgb: false));

        Assert.Contains("YUY2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A chain declaring more levels than the file holds is truncated, not rejected.</summary>
    [Fact]
    public void Read_ChainLongerThanTheFile_TruncatesToWhatIsPresent()
    {
        var file = DdsFixture.Build("DXT1", 16, 16, mipMapCount: 5, blockBytes: 8);
        var truncated = file.AsSpan(0, file.Length - 8).ToArray();

        var image = DdsReader.Read(truncated, isSrgb: false);

        Assert.Equal(4, image.Levels.Count);
    }
}
