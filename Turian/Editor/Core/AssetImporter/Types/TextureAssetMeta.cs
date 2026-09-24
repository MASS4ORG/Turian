namespace Turian.Editor.Core;

/// <summary>
/// Base metadata contract for texture assets.
/// </summary>
[TypeId("a3000002-0000-4000-8000-000000000002")]
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
