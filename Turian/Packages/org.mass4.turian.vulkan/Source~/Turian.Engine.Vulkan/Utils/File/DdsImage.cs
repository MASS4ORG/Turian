namespace Turian.Engine.Core;

/// <summary>
/// A decoded DDS file: the Vulkan format its pixel data already is in, the base extent, and one
/// slice per mip level. The slices are views over the source buffer — the block data is never
/// copied or decompressed.
/// </summary>
/// <param name="Format">Vulkan format matching the file's fourCC or DXGI format.</param>
/// <param name="Width">Width of mip level 0.</param>
/// <param name="Height">Height of mip level 0.</param>
/// <param name="Levels">One slice per mip level, largest first.</param>
public sealed record DdsImage(
    Format Format,
    uint Width,
    uint Height,
    IReadOnlyList<ReadOnlyMemory<byte>> Levels);
