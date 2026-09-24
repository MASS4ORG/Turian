namespace Turian.Engine.Core;

/// <summary>
/// 1×1 fallback textures bound to material slots with no texture reference. The PBR
/// shader multiplies each sample by the matching factor uniform, so these neutral
/// values reduce a slot to its factor alone without branching in the shader.
/// </summary>
public sealed class DefaultTextures : IDisposable
{
    /// <summary>White (255,255,255,255), sRGB. Use for missing base color / emissive.</summary>
    public Texture White { get; }

    /// <summary>Black (0,0,0,255), sRGB. Use for missing emissive when the factor is zero.</summary>
    public Texture Black { get; }

    /// <summary>Tangent-space "up" normal (128,128,255,255), linear. Use for missing normal map.</summary>
    public Texture FlatNormal { get; }

    /// <summary>(255, 255, 255, 255) linear: AO=1, Roughness=1, Metallic=1. The shader
    /// multiplies by the per-material factors, so this reduces to factor-only sampling.</summary>
    public Texture LinearWhite { get; }

    /// <summary>
    /// (255, 255, 0, 255) linear: AO=1, Roughness=1, Metallic=0. Bound to a metallic-roughness
    /// slot whose texture is referenced but could not be read, where sampling white would make
    /// the surface fully metallic and, without an environment map, black.
    /// </summary>
    public Texture Dielectric { get; }

    /// <summary>
    /// Creates default fallback textures on <paramref name="vulkan"/>'s device.
    /// </summary>
    public DefaultTextures(Vulkan vulkan)
    {
        ArgumentNullException.ThrowIfNull(vulkan);

        White = new Texture(vulkan, 1, 1, [255, 255, 255, 255], isSrgb: true);
        Black = new Texture(vulkan, 1, 1, [0, 0, 0, 255], isSrgb: true);
        FlatNormal = new Texture(vulkan, 1, 1, [128, 128, 255, 255], isSrgb: false);
        LinearWhite = new Texture(vulkan, 1, 1, [255, 255, 255, 255], isSrgb: false);
        Dielectric = new Texture(vulkan, 1, 1, [255, 255, 0, 255], isSrgb: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        White.Dispose();
        Black.Dispose();
        FlatNormal.Dispose();
        LinearWhite.Dispose();
        Dielectric.Dispose();
    }
}
