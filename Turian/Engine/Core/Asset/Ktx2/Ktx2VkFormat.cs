namespace Turian.Engine.Core;

/// <summary>
/// The <c>VkFormat</c> values a KTX2 container names, kept as plain numbers so the container code needs no
/// graphics dependency. Values outside this list still read; only the listed ones have a data format descriptor.
/// </summary>
public enum Ktx2VkFormat : uint
{
    /// <summary>No format; KTX2 uses it for Basis Universal payloads, which need transcoding.</summary>
    Undefined = 0,

    /// <summary>One 8-bit linear channel.</summary>
    R8Unorm = 9,

    /// <summary>Two 8-bit linear channels.</summary>
    R8G8Unorm = 16,

    /// <summary>Four 8-bit linear channels.</summary>
    R8G8B8A8Unorm = 37,

    /// <summary>Four 8-bit channels, color in sRGB space and alpha linear.</summary>
    R8G8B8A8Srgb = 43,

    /// <summary>BC1 opaque color, linear.</summary>
    Bc1RgbUnormBlock = 131,

    /// <summary>BC1 opaque color, sRGB.</summary>
    Bc1RgbSrgbBlock = 132,

    /// <summary>BC1 color with 1-bit alpha, linear.</summary>
    Bc1RgbaUnormBlock = 133,

    /// <summary>BC1 color with 1-bit alpha, sRGB.</summary>
    Bc1RgbaSrgbBlock = 134,

    /// <summary>BC2 color with explicit alpha, linear.</summary>
    Bc2UnormBlock = 135,

    /// <summary>BC2 color with explicit alpha, sRGB.</summary>
    Bc2SrgbBlock = 136,

    /// <summary>BC3 color with interpolated alpha, linear.</summary>
    Bc3UnormBlock = 137,

    /// <summary>BC3 color with interpolated alpha, sRGB.</summary>
    Bc3SrgbBlock = 138,

    /// <summary>BC4 single channel, unsigned.</summary>
    Bc4UnormBlock = 139,

    /// <summary>BC4 single channel, signed.</summary>
    Bc4SnormBlock = 140,

    /// <summary>BC5 two channels, unsigned.</summary>
    Bc5UnormBlock = 141,

    /// <summary>BC5 two channels, signed.</summary>
    Bc5SnormBlock = 142,

    /// <summary>BC6H high dynamic range color, unsigned float.</summary>
    Bc6hUfloatBlock = 143,

    /// <summary>BC6H high dynamic range color, signed float.</summary>
    Bc6hSfloatBlock = 144,

    /// <summary>BC7 color and alpha, linear.</summary>
    Bc7UnormBlock = 145,

    /// <summary>BC7 color and alpha, sRGB.</summary>
    Bc7SrgbBlock = 146,

    /// <summary>ETC2 opaque color, linear.</summary>
    Etc2R8G8B8UnormBlock = 147,

    /// <summary>ETC2 opaque color, sRGB.</summary>
    Etc2R8G8B8SrgbBlock = 148,

    /// <summary>ETC2 color with EAC alpha, linear.</summary>
    Etc2R8G8B8A8UnormBlock = 151,

    /// <summary>ETC2 color with EAC alpha, sRGB.</summary>
    Etc2R8G8B8A8SrgbBlock = 152,

    /// <summary>ASTC 4x4 blocks, linear.</summary>
    Astc4x4UnormBlock = 157,

    /// <summary>ASTC 4x4 blocks, sRGB.</summary>
    Astc4x4SrgbBlock = 158,

    /// <summary>ASTC 6x6 blocks, linear.</summary>
    Astc6x6UnormBlock = 165,

    /// <summary>ASTC 6x6 blocks, sRGB.</summary>
    Astc6x6SrgbBlock = 166,

    /// <summary>ASTC 8x8 blocks, linear.</summary>
    Astc8x8UnormBlock = 171,

    /// <summary>ASTC 8x8 blocks, sRGB.</summary>
    Astc8x8SrgbBlock = 172,
}
