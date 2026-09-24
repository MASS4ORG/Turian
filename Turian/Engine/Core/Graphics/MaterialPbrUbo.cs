namespace Turian.Engine.Core;

/// <summary>
/// Per-material uniform block bound at set=1, binding=0 of the PBR pipeline. Layout
/// is std140-compatible: the trailing scalars pack into a single vec4. Update this
/// struct and the matching <c>PbrMaterial</c> uniform block in the shader together.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct MaterialPbrUbo
{
    /// <summary>RGBA base color factor multiplied with the base color texture sample.</summary>
    public Vector4 BaseColorFactor;

    /// <summary>Emissive RGB factor multiplied with the emissive texture sample. <c>W</c> is unused (padding).</summary>
    public Vector4 EmissiveFactor;

    /// <summary>Metallic factor in [0,1].</summary>
    public float MetallicFactor;

    /// <summary>Roughness factor in [0,1].</summary>
    public float RoughnessFactor;

    /// <summary>Occlusion strength in [0,1] (glTF default 1.0).</summary>
    public float OcclusionStrength;

    /// <summary>
    /// Non-zero when the normal map's green channel is inverted, as DirectX-convention maps are.
    /// A flag rather than an import-time rewrite because block-compressed normals cannot be edited
    /// without recompressing them. Also occupies the padding that keeps the trailing scalars in one
    /// std140 vec4.
    /// </summary>
    public float FlipGreenChannel;
}
