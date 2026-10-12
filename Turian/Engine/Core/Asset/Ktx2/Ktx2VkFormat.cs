namespace Turian.Engine.Core;

/// <summary>
/// The <c>VkFormat</c> values a KTX2 container names, kept as plain numbers so the container code needs no
/// graphics dependency. Values outside this list still read; only the listed ones have a data format descriptor.
/// </summary>
public enum Ktx2VkFormat : uint
{
    /// <summary>No format; KTX2 uses it for Basis Universal payloads, which need transcoding.</summary>
    Undefined = 0,

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

    /// <summary>BC7 color and alpha, linear.</summary>
    Bc7UnormBlock = 145,

    /// <summary>BC7 color and alpha, sRGB.</summary>
    Bc7SrgbBlock = 146,
}
