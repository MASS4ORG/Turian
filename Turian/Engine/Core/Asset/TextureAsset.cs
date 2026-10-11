namespace Turian.Engine.Core;

/// <summary>
/// A 2D texture asset: the settings its importer and the render backend's upload read. Uploading lives with the
/// backend, so a host without one never decodes texture data.
/// </summary>
[TypeId("f12b8bbf-74b4-5af6-95a3-535c4fa6c16c")]
public class TextureAsset : Asset
{
    /// <summary>
    /// Whether the texture is sampled in sRGB color space. True for albedo /
    /// base-color / emissive textures; false for normal, metallic-roughness,
    /// and other linear data textures.
    /// </summary>
    public bool IsSrgb { get; set; } = true;

    /// <summary>
    /// Whether mipmaps should be generated when the texture is uploaded.
    /// </summary>
    public bool GenerateMips { get; set; } = true;

    /// <summary>
    /// Whether the green channel is inverted relative to the OpenGL/glTF normal-map convention.
    /// True for DirectX-convention normal maps. Applied by the shader, since block-compressed data
    /// cannot be rewritten at import without recompressing it.
    /// </summary>
    public bool FlipGreenChannel { get; set; }

    /// <summary>
    /// How the importer bakes this texture into cache artifacts.
    /// </summary>
    public TextureImportSettings ImportSettings { get; set; } = new();
}
