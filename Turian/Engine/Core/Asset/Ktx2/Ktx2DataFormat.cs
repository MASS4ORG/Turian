namespace Turian.Engine.Core;

/// <summary>
/// Builds the Khronos Basic Data Format Descriptor every KTX2 file carries for its <see cref="Ktx2VkFormat"/>,
/// byte for byte as KTX-Software's <c>dfdutils</c> emits it, and answers the block-shape questions a writer asks.
/// </summary>
public static class Ktx2DataFormat
{
    const uint versionNumber = 2;
    const uint primariesBt709 = 1;
    const uint transferLinear = 1;
    const uint transferSrgb = 2;
    const int blockHeaderLength = 24;
    const int sampleLength = 16;

    const byte modelRgbsda = 1;
    const byte modelBc1A = 128;
    const byte modelBc2 = 129;
    const byte modelBc3 = 130;
    const byte modelBc4 = 131;
    const byte modelBc5 = 132;
    const byte modelBc6H = 133;
    const byte modelBc7 = 134;
    const byte modelEtc2 = 161;
    const byte modelAstc = 162;
    const byte channelEtc2Color = 2;

    const byte channelAlpha = 15;
    const byte qualifierLinear = 0x10;
    const byte qualifierSigned = 0x40;
    const byte qualifierFloat = 0x80;

    enum Numeric { Unorm, Srgb, Snorm, Ufloat, Sfloat }

    readonly record struct Sample(byte Channel, ushort BitOffset, byte BitLength);

    sealed record Layout(byte Model, byte BlockWidth, byte BlockHeight, byte BlockBytes, Numeric Numeric,
        Sample[] Samples);

    static readonly Dictionary<Ktx2VkFormat, Layout> layouts = new()
    {
        [Ktx2VkFormat.R8Unorm] = Rgbsda(1, Numeric.Unorm),
        [Ktx2VkFormat.R8G8Unorm] = Rgbsda(2, Numeric.Unorm),
        [Ktx2VkFormat.R8G8B8A8Unorm] = Rgbsda(4, Numeric.Unorm),
        [Ktx2VkFormat.R8G8B8A8Srgb] = Rgbsda(4, Numeric.Srgb),
        [Ktx2VkFormat.Bc1RgbUnormBlock] = Block(modelBc1A, 8, Numeric.Unorm, At(0, 0, 64)),
        [Ktx2VkFormat.Bc1RgbSrgbBlock] = Block(modelBc1A, 8, Numeric.Srgb, At(0, 0, 64)),
        [Ktx2VkFormat.Bc1RgbaUnormBlock] = Block(modelBc1A, 8, Numeric.Unorm, At(1, 0, 64)),
        [Ktx2VkFormat.Bc1RgbaSrgbBlock] = Block(modelBc1A, 8, Numeric.Srgb, At(1, 0, 64)),
        [Ktx2VkFormat.Bc2UnormBlock] = Block(modelBc2, 16, Numeric.Unorm, At(channelAlpha, 0, 64), At(0, 64, 64)),
        [Ktx2VkFormat.Bc2SrgbBlock] = Block(modelBc2, 16, Numeric.Srgb, At(channelAlpha, 0, 64), At(0, 64, 64)),
        [Ktx2VkFormat.Bc3UnormBlock] = Block(modelBc3, 16, Numeric.Unorm, At(channelAlpha, 0, 64), At(0, 64, 64)),
        [Ktx2VkFormat.Bc3SrgbBlock] = Block(modelBc3, 16, Numeric.Srgb, At(channelAlpha, 0, 64), At(0, 64, 64)),
        [Ktx2VkFormat.Bc4UnormBlock] = Block(modelBc4, 8, Numeric.Unorm, At(0, 0, 64)),
        [Ktx2VkFormat.Bc4SnormBlock] = Block(modelBc4, 8, Numeric.Snorm, At(0, 0, 64)),
        [Ktx2VkFormat.Bc5UnormBlock] = Block(modelBc5, 16, Numeric.Unorm, At(0, 0, 64), At(1, 64, 64)),
        [Ktx2VkFormat.Bc5SnormBlock] = Block(modelBc5, 16, Numeric.Snorm, At(0, 0, 64), At(1, 64, 64)),
        [Ktx2VkFormat.Bc7UnormBlock] = Block(modelBc7, 16, Numeric.Unorm, At(0, 0, 128)),
        [Ktx2VkFormat.Bc6hUfloatBlock] = Block(modelBc6H, 16, Numeric.Ufloat, At(0, 0, 128)),
        [Ktx2VkFormat.Bc6hSfloatBlock] = Block(modelBc6H, 16, Numeric.Sfloat, At(0, 0, 128)),
        [Ktx2VkFormat.Bc7SrgbBlock] = Block(modelBc7, 16, Numeric.Srgb, At(0, 0, 128)),
        [Ktx2VkFormat.Etc2R8G8B8UnormBlock] = Block(modelEtc2, 8, Numeric.Unorm, At(channelEtc2Color, 0, 64)),
        [Ktx2VkFormat.Etc2R8G8B8SrgbBlock] = Block(modelEtc2, 8, Numeric.Srgb, At(channelEtc2Color, 0, 64)),
        [Ktx2VkFormat.Etc2R8G8B8A8UnormBlock] =
            Block(modelEtc2, 16, Numeric.Unorm, At(channelAlpha, 0, 64), At(channelEtc2Color, 64, 64)),
        [Ktx2VkFormat.Etc2R8G8B8A8SrgbBlock] =
            Block(modelEtc2, 16, Numeric.Srgb, At(channelAlpha, 0, 64), At(channelEtc2Color, 64, 64)),
        [Ktx2VkFormat.Astc4x4UnormBlock] = Astc(4, Numeric.Unorm),
        [Ktx2VkFormat.Astc4x4SrgbBlock] = Astc(4, Numeric.Srgb),
        [Ktx2VkFormat.Astc6x6UnormBlock] = Astc(6, Numeric.Unorm),
        [Ktx2VkFormat.Astc6x6SrgbBlock] = Astc(6, Numeric.Srgb),
        [Ktx2VkFormat.Astc8x8UnormBlock] = Astc(8, Numeric.Unorm),
        [Ktx2VkFormat.Astc8x8SrgbBlock] = Astc(8, Numeric.Srgb),
    };

    /// <summary>Whether a descriptor can be built for <paramref name="format"/>, so a writer can emit it.</summary>
    /// <param name="format">The format to check.</param>
    public static bool IsSupported(Ktx2VkFormat format) => layouts.ContainsKey(format);

    /// <summary>Whether <paramref name="format"/> stores color in sRGB space.</summary>
    /// <param name="format">A supported format.</param>
    /// <exception cref="NotSupportedException">The format has no descriptor.</exception>
    public static bool IsSrgb(Ktx2VkFormat format) => Describe(format).Numeric == Numeric.Srgb;

    /// <summary>
    /// The texel block of <paramref name="format"/>: its extent in texels and its size in bytes. Uncompressed formats
    /// have a 1x1 block, one texel.
    /// </summary>
    /// <param name="format">A supported format.</param>
    /// <exception cref="NotSupportedException">The format has no descriptor.</exception>
    public static (uint Width, uint Height, uint Bytes) BlockShape(Ktx2VkFormat format)
    {
        var layout = Describe(format);
        return (layout.BlockWidth, layout.BlockHeight, layout.BlockBytes);
    }

    /// <summary>
    /// Bytes of one tightly packed <paramref name="width"/>x<paramref name="height"/> image in
    /// <paramref name="format"/>, rounding up to whole texel blocks.
    /// </summary>
    /// <param name="format">A supported format.</param>
    /// <param name="width">Image width in texels.</param>
    /// <param name="height">Image height in texels.</param>
    public static ulong ImageSize(Ktx2VkFormat format, uint width, uint height)
    {
        var (blockWidth, blockHeight, bytes) = BlockShape(format);
        var blocksWide = (Math.Max(width, 1u) + blockWidth - 1) / blockWidth;
        var blocksHigh = (Math.Max(height, 1u) + blockHeight - 1) / blockHeight;
        return (ulong)blocksWide * blocksHigh * bytes;
    }

    /// <summary>
    /// Builds the complete descriptor, including its leading total-size word, as it is stored in a KTX2 file.
    /// </summary>
    /// <param name="format">A supported format.</param>
    /// <exception cref="NotSupportedException">The format has no descriptor.</exception>
    public static byte[] Build(Ktx2VkFormat format)
    {
        var layout = Describe(format);
        var blockSize = blockHeaderLength + (sampleLength * layout.Samples.Length);
        var descriptor = new byte[4 + blockSize];
        var span = descriptor.AsSpan();

        var transfer = layout.Numeric == Numeric.Srgb ? transferSrgb : transferLinear;
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], versionNumber | ((uint)blockSize << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], layout.Model | (primariesBt709 << 8) | (transfer << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..],
            (uint)(layout.BlockWidth - 1) | ((uint)(layout.BlockHeight - 1) << 8));
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], layout.BlockBytes);

        for (var index = 0; index < layout.Samples.Length; index++)
        {
            var sampleStart = 4 + blockHeaderLength + (sampleLength * index);
            WriteSample(span[sampleStart..], layout.Samples[index], layout.Numeric);
        }

        return descriptor;
    }

    static void WriteSample(Span<byte> destination, Sample sample, Numeric numeric)
    {
        var channelType = (uint)(sample.Channel | Qualifiers(numeric, sample.Channel));
        var (lower, upper) = Bounds(numeric, sample.BitLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination,
            sample.BitOffset | ((uint)(sample.BitLength - 1) << 16) | (channelType << 24));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], lower);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], upper);
    }

    /// <summary>sRGB transfer applies to color only, so an sRGB format marks its alpha sample linear.</summary>
    static byte Qualifiers(Numeric numeric, byte channel) => numeric switch
    {
        Numeric.Snorm => qualifierSigned,
        Numeric.Ufloat => qualifierFloat,
        Numeric.Sfloat => qualifierSigned | qualifierFloat,
        Numeric.Srgb when channel == channelAlpha => qualifierLinear,
        _ => 0,
    };

    static (uint Lower, uint Upper) Bounds(Numeric numeric, byte bitLength) => numeric switch
    {
        Numeric.Snorm => (0x80000000u, 0x7FFFFFFFu),
        Numeric.Ufloat => (0u, BitConverter.SingleToUInt32Bits(1f)),
        Numeric.Sfloat => (BitConverter.SingleToUInt32Bits(-1f), BitConverter.SingleToUInt32Bits(1f)),
        _ => (0u, bitLength >= 32 ? uint.MaxValue : (1u << bitLength) - 1),
    };

    static Layout Describe(Ktx2VkFormat format) =>
        layouts.TryGetValue(format, out var layout)
            ? layout
            : throw new NotSupportedException($"No KTX2 data format descriptor for VkFormat {(uint)format}.");

    static Layout Rgbsda(int channels, Numeric numeric)
    {
        var samples = new Sample[channels];
        for (var index = 0; index < channels; index++)
        {
            var channel = index == 3 ? channelAlpha : (byte)index;
            samples[index] = new Sample(channel, (ushort)(index * 8), 8);
        }

        return new Layout(modelRgbsda, 1, 1, (byte)channels, numeric, samples);
    }

    static Layout Block(byte model, byte bytes, Numeric numeric, params Sample[] samples) =>
        new(model, 4, 4, bytes, numeric, samples);

    static Layout Astc(byte blockExtent, Numeric numeric) =>
        new(modelAstc, blockExtent, blockExtent, 16, numeric, [At(0, 0, 128)]);

    static Sample At(byte channel, ushort bitOffset, byte bitLength) => new(channel, bitOffset, bitLength);
}
