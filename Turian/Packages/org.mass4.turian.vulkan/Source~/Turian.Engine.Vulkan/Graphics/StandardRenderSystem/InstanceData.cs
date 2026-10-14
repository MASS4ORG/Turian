namespace Turian.Engine.Core;

/// <summary>
/// Per-instance shader data read at set 2, binding 0 by <c>gl_InstanceIndex</c>; must match <c>InstanceData</c> in
/// standardShader.vert (std430, 160 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct InstanceData
{
    /// <summary>The byte size of one instance.</summary>
    public const int Size = 160;

    /// <summary>The model-to-world matrix.</summary>
    public Matrix4x4 Model;

    /// <summary>The normal matrix; its unused final element carries the render layer bits.</summary>
    public Matrix4x4 Normal;

    /// <summary>The linear tint multiplied into the base color.</summary>
    public Vector4 Color;

    /// <summary>Free values passed to the shader unchanged.</summary>
    public Vector4 Custom;

    /// <summary>Packs one instance, storing the render layer's mask bits in the normal matrix's final element.</summary>
    public static InstanceData Create(
        in Matrix4x4 model, Matrix4x4 normal, int renderLayer, Color color, Vector4 custom)
    {
        normal.M44 = BitConverter.UInt32BitsToSingle(LayerMask.FromLayer(renderLayer).Value);
        return new InstanceData
        {
            Model = model,
            Normal = normal,
            Color = new Vector4(color.R, color.G, color.B, color.A),
            Custom = custom,
        };
    }
}
