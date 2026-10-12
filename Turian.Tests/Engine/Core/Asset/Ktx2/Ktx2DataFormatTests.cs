namespace Turian.Tests;

/// <summary>
/// Compares built data format descriptors with the ones KTX-Software v4.4.2 writes (<c>ktx create --raw</c>, read
/// back as little-endian 32-bit words), so files from either tool describe a format identically.
/// </summary>
public class Ktx2DataFormatTests
{
    const string bc1 =
        "0000002c 00000000 00280002 000{0}0180 00000303 00000008 00000000 {1}3f0000 00000000 00000000 ffffffff";

    const string single128 =
        "0000002c 00000000 00280002 000{0}01{1} 00000{2} 00000010 00000000 {3}7f0000 00000000 {4} {5}";

    /// <summary>The reference words, one row per format.</summary>
    public static TheoryData<Ktx2VkFormat, string> References() => new()
    {
        {
            Ktx2VkFormat.R8Unorm,
            "0000002c 00000000 00280002 00010101 00000000 00000001 00000000 00070000 00000000 00000000 000000ff"
        },
        {
            Ktx2VkFormat.R8G8Unorm,
            "0000003c 00000000 00380002 00010101 00000000 00000002 00000000 00070000 00000000 00000000 000000ff " +
            "01070008 00000000 00000000 000000ff"
        },
        { Ktx2VkFormat.R8G8B8A8Unorm, Rgba8("0001", "0f") },
        { Ktx2VkFormat.R8G8B8A8Srgb, Rgba8("0002", "1f") },
        { Ktx2VkFormat.Bc1RgbUnormBlock, Bc1("1", "00") },
        { Ktx2VkFormat.Bc1RgbSrgbBlock, Bc1("2", "00") },
        { Ktx2VkFormat.Bc1RgbaUnormBlock, Bc1("1", "01") },
        { Ktx2VkFormat.Bc1RgbaSrgbBlock, Bc1("2", "01") },
        { Ktx2VkFormat.Bc2UnormBlock, TwoSample64("00010181", "00000010", "0f", "00", "00000000 ffffffff") },
        { Ktx2VkFormat.Bc2SrgbBlock, TwoSample64("00020181", "00000010", "1f", "00", "00000000 ffffffff") },
        { Ktx2VkFormat.Bc3UnormBlock, TwoSample64("00010182", "00000010", "0f", "00", "00000000 ffffffff") },
        { Ktx2VkFormat.Bc3SrgbBlock, TwoSample64("00020182", "00000010", "1f", "00", "00000000 ffffffff") },
        {
            Ktx2VkFormat.Bc4UnormBlock,
            "0000002c 00000000 00280002 00010183 00000303 00000008 00000000 003f0000 00000000 00000000 ffffffff"
        },
        {
            Ktx2VkFormat.Bc4SnormBlock,
            "0000002c 00000000 00280002 00010183 00000303 00000008 00000000 403f0000 00000000 80000000 7fffffff"
        },
        { Ktx2VkFormat.Bc5UnormBlock, TwoSample64("00010184", "00000010", "00", "01", "00000000 ffffffff") },
        { Ktx2VkFormat.Bc5SnormBlock, TwoSample64("00010184", "00000010", "40", "41", "80000000 7fffffff") },
        { Ktx2VkFormat.Bc6hUfloatBlock, Single128("1", "85", "303", "80", "00000000", "3f800000") },
        { Ktx2VkFormat.Bc6hSfloatBlock, Single128("1", "85", "303", "c0", "bf800000", "3f800000") },
        { Ktx2VkFormat.Bc7UnormBlock, Single128("1", "86", "303", "00", "00000000", "ffffffff") },
        { Ktx2VkFormat.Bc7SrgbBlock, Single128("2", "86", "303", "00", "00000000", "ffffffff") },
        {
            Ktx2VkFormat.Etc2R8G8B8UnormBlock,
            "0000002c 00000000 00280002 000101a1 00000303 00000008 00000000 023f0000 00000000 00000000 ffffffff"
        },
        {
            Ktx2VkFormat.Etc2R8G8B8SrgbBlock,
            "0000002c 00000000 00280002 000201a1 00000303 00000008 00000000 023f0000 00000000 00000000 ffffffff"
        },
        { Ktx2VkFormat.Etc2R8G8B8A8UnormBlock, TwoSample64("000101a1", "00000010", "0f", "02", "00000000 ffffffff") },
        { Ktx2VkFormat.Etc2R8G8B8A8SrgbBlock, TwoSample64("000201a1", "00000010", "1f", "02", "00000000 ffffffff") },
        { Ktx2VkFormat.Astc4x4UnormBlock, Single128("1", "a2", "303", "00", "00000000", "ffffffff") },
        { Ktx2VkFormat.Astc4x4SrgbBlock, Single128("2", "a2", "303", "00", "00000000", "ffffffff") },
        { Ktx2VkFormat.Astc6x6UnormBlock, Single128("1", "a2", "505", "00", "00000000", "ffffffff") },
        { Ktx2VkFormat.Astc6x6SrgbBlock, Single128("2", "a2", "505", "00", "00000000", "ffffffff") },
        { Ktx2VkFormat.Astc8x8UnormBlock, Single128("1", "a2", "707", "00", "00000000", "ffffffff") },
        { Ktx2VkFormat.Astc8x8SrgbBlock, Single128("2", "a2", "707", "00", "00000000", "ffffffff") },
    };

    static string Rgba8(string transfer, string alpha) =>
        $"0000005c 00000000 00580002 {transfer}0101 00000000 00000004 00000000 00070000 00000000 00000000 000000ff " +
        "01070008 00000000 00000000 000000ff 02070010 00000000 00000000 000000ff " +
        $"{alpha}070018 00000000 00000000 000000ff";

    static string Bc1(string transfer, string channel) =>
        string.Format(CultureInfo.InvariantCulture, bc1, transfer, channel);

    static string Single128(string transfer, string model, string extent, string channelType, string lower,
        string upper) =>
        string.Format(CultureInfo.InvariantCulture, single128, transfer, model, extent, channelType, lower, upper);

    static string TwoSample64(string modelWord, string bytesWord, string first, string second, string bounds) =>
        $"0000003c 00000000 00380002 {modelWord} 00000303 {bytesWord} 00000000 {first}3f0000 00000000 {bounds} " +
        $"{second}3f0040 00000000 {bounds}";

    static uint[] Words(byte[] descriptor)
    {
        var words = new uint[descriptor.Length / 4];
        for (var index = 0; index < words.Length; index++)
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(index * 4));
        return words;
    }

    /// <summary>Every format Turian emits has a descriptor equal to KTX-Software's, word for word.</summary>
    [Theory]
    [MemberData(nameof(References))]
    public void Build_MatchesKtxSoftware(Ktx2VkFormat format, string reference)
    {
        var expected = reference.Split(' ').Select(word => uint.Parse(word, NumberStyles.HexNumber)).ToArray();

        Assert.Equal(expected, Words(Ktx2DataFormat.Build(format)));
    }

    /// <summary>The reference table covers every format with a descriptor, so none skips the check.</summary>
    [Fact]
    public void References_CoverEverySupportedFormat()
    {
        var covered = References().Select(row => row.Data.Item1).ToHashSet();

        Assert.All(Enum.GetValues<Ktx2VkFormat>().Where(Ktx2DataFormat.IsSupported), f => Assert.Contains(f, covered));
    }

    /// <summary>Block shapes follow the format family: ASTC footprints, 4x4 BCn and ETC2, single texels.</summary>
    [Theory]
    [InlineData(Ktx2VkFormat.R8Unorm, 1u, 1u, 1u)]
    [InlineData(Ktx2VkFormat.R8G8Unorm, 1u, 1u, 2u)]
    [InlineData(Ktx2VkFormat.Etc2R8G8B8UnormBlock, 4u, 4u, 8u)]
    [InlineData(Ktx2VkFormat.Astc6x6SrgbBlock, 6u, 6u, 16u)]
    [InlineData(Ktx2VkFormat.Astc8x8UnormBlock, 8u, 8u, 16u)]
    public void BlockShape_FollowsTheFormatFamily(Ktx2VkFormat format, uint width, uint height, uint bytes)
    {
        Assert.Equal((width, height, bytes), Ktx2DataFormat.BlockShape(format));
    }

    /// <summary>A 10x10 ASTC 6x6 image rounds up to four blocks.</summary>
    [Fact]
    public void ImageSize_RoundsUpToWholeBlocks()
    {
        Assert.Equal(64ul, Ktx2DataFormat.ImageSize(Ktx2VkFormat.Astc6x6UnormBlock, 10, 10));
        Assert.Equal(16ul, Ktx2DataFormat.ImageSize(Ktx2VkFormat.Astc8x8SrgbBlock, 1, 1));
    }
}
