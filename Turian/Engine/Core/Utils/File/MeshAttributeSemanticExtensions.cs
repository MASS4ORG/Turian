namespace Turian.Engine.Core;

/// <summary>
/// Converts between <see cref="MeshAttributeSemantic"/> and glTF 2.0 attribute names.
/// </summary>
public static class MeshAttributeSemanticExtensions
{
    /// <summary>Returns the glTF 2.0 attribute name for <paramref name="semantic"/>.</summary>
    public static string ToGltfName(this MeshAttributeSemantic semantic) => semantic switch
    {
        MeshAttributeSemantic.Position => "POSITION",
        MeshAttributeSemantic.Normal => "NORMAL",
        MeshAttributeSemantic.Tangent => "TANGENT",
        MeshAttributeSemantic.TexCoord0 => "TEXCOORD_0",
        MeshAttributeSemantic.TexCoord1 => "TEXCOORD_1",
        MeshAttributeSemantic.Color0 => "COLOR_0",
        _ => throw new ArgumentOutOfRangeException(nameof(semantic), semantic, null),
    };

    /// <summary>
    /// Resolves a glTF 2.0 attribute name to its <see cref="MeshAttributeSemantic"/>.
    /// </summary>
    /// <param name="gltfName">The glTF attribute name, for example <c>TEXCOORD_0</c>.</param>
    /// <param name="semantic">The resolved semantic when the name is known.</param>
    /// <returns><c>true</c> when the name maps to a semantic; otherwise <c>false</c>.</returns>
    public static bool TryFromGltfName(string gltfName, out MeshAttributeSemantic semantic)
    {
        switch (gltfName)
        {
            case "POSITION": semantic = MeshAttributeSemantic.Position; return true;
            case "NORMAL": semantic = MeshAttributeSemantic.Normal; return true;
            case "TANGENT": semantic = MeshAttributeSemantic.Tangent; return true;
            case "TEXCOORD_0": semantic = MeshAttributeSemantic.TexCoord0; return true;
            case "TEXCOORD_1": semantic = MeshAttributeSemantic.TexCoord1; return true;
            case "COLOR_0": semantic = MeshAttributeSemantic.Color0; return true;
            default: semantic = default; return false;
        }
    }

    /// <summary>
    /// Returns the glTF 2.0 accessor <c>type</c> string for a component count.
    /// </summary>
    /// <param name="componentCount">Number of components per element (1 to 4).</param>
    public static string ToGltfAccessorType(uint componentCount) => componentCount switch
    {
        1 => "SCALAR",
        2 => "VEC2",
        3 => "VEC3",
        4 => "VEC4",
        _ => throw new ArgumentOutOfRangeException(nameof(componentCount), componentCount, null),
    };
}
