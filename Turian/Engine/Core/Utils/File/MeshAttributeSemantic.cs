namespace Turian.Engine.Core;

/// <summary>
/// A vertex attribute semantic. Each value maps one-to-one onto a glTF 2.0
/// mesh-primitive attribute name.
/// </summary>
public enum MeshAttributeSemantic
{
    /// <summary>glTF <c>POSITION</c>.</summary>
    Position = 0,

    /// <summary>glTF <c>NORMAL</c>.</summary>
    Normal = 1,

    /// <summary>glTF <c>TANGENT</c>.</summary>
    Tangent = 2,

    /// <summary>glTF <c>TEXCOORD_0</c>.</summary>
    TexCoord0 = 3,

    /// <summary>glTF <c>TEXCOORD_1</c>.</summary>
    TexCoord1 = 4,

    /// <summary>glTF <c>COLOR_0</c>.</summary>
    Color0 = 5,
}
