namespace Turian.Editor.Core;

/// <summary>
/// The texture data a <see cref="TextureBlobWriter"/> bakes into an <c>.amtex</c> container:
/// a format, the base extent and the mip levels in the layout the GPU samples.
/// </summary>
/// <param name="Format">Vulkan format of the stored levels.</param>
/// <param name="Width">Width of mip level 0.</param>
/// <param name="Height">Height of mip level 0.</param>
/// <param name="IsSrgb">Whether the data is sampled in sRGB space.</param>
/// <param name="Levels">The mip levels, largest first. At least one.</param>
public sealed record TextureBlobContent(
    Format Format,
    uint Width,
    uint Height,
    bool IsSrgb,
    IReadOnlyList<ReadOnlyMemory<byte>> Levels);
