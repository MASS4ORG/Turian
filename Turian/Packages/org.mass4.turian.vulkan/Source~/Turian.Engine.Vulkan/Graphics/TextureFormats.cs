namespace Turian.Engine.Core;

/// <summary>
/// Size arithmetic for the texture formats the engine uploads. Block-compressed formats are
/// measured in 4×4 blocks rather than texels, so a mip level's byte count is the block count
/// rounded up, never a row pitch.
/// </summary>
public static class TextureFormats
{
    const uint blockExtent = 4;

    /// <summary>
    /// Reports whether <paramref name="format"/> stores 4×4 block-compressed data.
    /// </summary>
    /// <param name="format">The format to classify.</param>
    public static bool IsBlockCompressed(Format format) => format switch
    {
        Format.BC1RgbUnormBlock or Format.BC1RgbSrgbBlock
            or Format.BC1RgbaUnormBlock or Format.BC1RgbaSrgbBlock
            or Format.BC2UnormBlock or Format.BC2SrgbBlock
            or Format.BC3UnormBlock or Format.BC3SrgbBlock
            or Format.BC4UnormBlock or Format.BC4SNormBlock
            or Format.BC5UnormBlock or Format.BC5SNormBlock
            or Format.BC7UnormBlock or Format.BC7SrgbBlock => true,
        _ => false,
    };

    /// <summary>
    /// Bytes occupied by one element of <paramref name="format"/> — one 4×4 block for compressed
    /// formats, one texel otherwise.
    /// </summary>
    /// <param name="format">The format to measure.</param>
    /// <exception cref="NotSupportedException">The format is not one the engine uploads.</exception>
    public static uint ElementSizeBytes(Format format) => format switch
    {
        Format.BC1RgbUnormBlock or Format.BC1RgbSrgbBlock
            or Format.BC1RgbaUnormBlock or Format.BC1RgbaSrgbBlock
            or Format.BC4UnormBlock or Format.BC4SNormBlock => 8,
        Format.BC2UnormBlock or Format.BC2SrgbBlock
            or Format.BC3UnormBlock or Format.BC3SrgbBlock
            or Format.BC5UnormBlock or Format.BC5SNormBlock
            or Format.BC7UnormBlock or Format.BC7SrgbBlock => 16,
        Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => 4,
        Format.R8Unorm => 1,
        Format.R8G8Unorm => 2,
        _ => throw new NotSupportedException($"Unsupported texture format: {format}"),
    };

    /// <summary>
    /// Bytes occupied by a tightly-packed <paramref name="width"/>×<paramref name="height"/> mip
    /// level of <paramref name="format"/>, rounding compressed extents up to whole 4×4 blocks.
    /// </summary>
    /// <param name="format">The format of the level.</param>
    /// <param name="width">Level width in texels.</param>
    /// <param name="height">Level height in texels.</param>
    public static uint LevelSizeBytes(Format format, uint width, uint height)
    {
        var elementSize = ElementSizeBytes(format);
        if (!IsBlockCompressed(format))
        {
            return width * height * elementSize;
        }

        var blocksWide = Math.Max(1u, (width + blockExtent - 1) / blockExtent);
        var blocksHigh = Math.Max(1u, (height + blockExtent - 1) / blockExtent);
        return blocksWide * blocksHigh * elementSize;
    }

    /// <summary>
    /// Extent of mip level <paramref name="level"/> of a <paramref name="width"/>×
    /// <paramref name="height"/> base image, never smaller than 1×1.
    /// </summary>
    /// <param name="width">Base level width.</param>
    /// <param name="height">Base level height.</param>
    /// <param name="level">Zero-based mip level.</param>
    public static (uint Width, uint Height) LevelExtent(uint width, uint height, int level) =>
        (Math.Max(1u, width >> level), Math.Max(1u, height >> level));

    /// <summary>
    /// Number of mip levels in a complete chain down to 1×1 for the given base extent.
    /// </summary>
    /// <param name="width">Base level width.</param>
    /// <param name="height">Base level height.</param>
    public static uint FullMipChainLength(uint width, uint height) =>
        (uint)(BitOperations.Log2(Math.Max(1u, Math.Max(width, height))) + 1);
}
