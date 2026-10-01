namespace Turian.Editor.Core;

/// <summary>
/// Base metadata contract for texture assets.
/// </summary>
[TypeId("ea57af64-61c4-5007-89f2-2dd02475979b")]
[PublicAPI]
public class TextureAssetMeta : Asset
{
    /// <summary>
    /// Gets or sets the texture format.
    /// </summary>
    public TextureAssetFormat TextureFormat { get; set; } = TextureAssetFormat.Unknown;

    /// <summary>
    /// Gets or sets a value indicating whether mip maps should be generated.
    /// </summary>
    public bool GenerateMipMaps { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the texture uses sRGB.
    /// </summary>
    public bool UseSrgb { get; set; } = true;
}
