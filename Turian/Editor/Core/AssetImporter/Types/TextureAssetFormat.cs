namespace Turian.Editor.Core;

/// <summary>
/// Specifies the supported file formats for texture assets.
/// </summary>
public enum TextureAssetFormat
{
    /// <summary>The format is unspecified or could not be determined.</summary>
    Unknown = 0,

    /// <summary>Portable Network Graphics. Supports lossless compression and alpha transparency.</summary>
    Png,

    /// <summary>Joint Photographic Experts Group. High compression, but does not support transparency.</summary>
    Jpeg,

    /// <summary>Truevision Graphics Adapter. Often used in older game engines for uncompressed data.</summary>
    Tga,

    /// <summary>Windows Bitmap. A legacy uncompressed raster format.</summary>
    Bmp,

    /// <summary>Graphics Interchange Format. Supports basic animation and 1-bit transparency.</summary>
    Gif,

    /// <summary>Web Picture format. Modern format providing superior lossy and lossless compression.</summary>
    Webp,

    /// <summary>DirectDraw Surface. The standard container for GPU-compressed textures (DXT/BC).</summary>
    Dds
}
