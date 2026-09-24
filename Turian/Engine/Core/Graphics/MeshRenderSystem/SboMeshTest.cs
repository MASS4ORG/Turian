namespace Turian.Engine.Core;

/// <summary>
/// Represents a test mesh structure used for shader binding.
/// </summary>
[PublicAPI]
public struct SboMeshTest
{
    /// <summary>
    /// Gets or sets the triangle count.
    /// </summary>
    public uint TriangleCount { get; set; }

    /// <summary>
    /// Gets or sets the spacing between triangles.
    /// </summary>
    public float TriangleSpacing { get; set; }

    /// <summary>
    /// Gets or sets the width of a triangle.
    /// </summary>
    public float TriangleWidth { get; set; }

    /// <summary>
    /// Gets or sets the height of a triangle.
    /// </summary>
    public float TriangleHeight { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SboMeshTest"/> struct.
    /// Sets the <see cref="TriangleCount"/>, <see cref="TriangleSpacing"/>,
    /// <see cref="TriangleWidth"/>, and <see cref="TriangleHeight"/> to default values.
    /// </summary>
    public SboMeshTest()
    {
        TriangleCount = 3;
        TriangleSpacing = 1f;
        TriangleWidth = 1f;
        TriangleHeight = 1f;
    }

    /// <summary>
    /// Gets the size of the <see cref="StandardPushConstantData"/> struct.
    /// </summary>
    /// <returns>The size of the <see cref="StandardPushConstantData"/> struct in bytes.</returns>
    public static uint SizeOf() => (uint)Unsafe.SizeOf<StandardPushConstantData>();
}
