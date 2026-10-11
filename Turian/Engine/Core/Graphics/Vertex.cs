namespace Turian.Engine.Core;

/// <summary>
/// Represents a vertex with position, color, normal, and UV attributes.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="Vertex"/> struct with the specified position and color.
/// </remarks>
/// <param name="Pos">The position of the vertex.</param>
/// <param name="Color">The color of the vertex.</param>
public record struct Vertex(Vector3 Pos, Vector3 Color)
{
    /// <summary>
    /// The position of the vertex in 3D space.
    /// </summary>
    public Vector3 Position = Pos;

    /// <summary>
    /// The color of the vertex.
    /// </summary>
    public Vector3 Color = Color;

    /// <summary>
    /// The normal vector of the vertex.
    /// </summary>
    public Vector3 Normal;

    /// <summary>
    /// The UV (texture coordinates) of the vertex.
    /// </summary>
    public Vector2 Uv;

    /// <summary>
    /// The tangent vector (XYZ) plus handedness sign in W (glTF convention).
    /// </summary>
    public Vector4 Tangent;

    /// <summary>
    /// Gets the size in bytes of the <see cref="Vertex"/> struct.
    /// </summary>
    /// <returns>The size of the struct in bytes.</returns>
    public static uint SizeOf() => (uint)Unsafe.SizeOf<Vertex>();
}
