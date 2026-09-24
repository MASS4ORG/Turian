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

    /// <summary>
    /// Gets an array of vertex input binding descriptions for this vertex format.
    /// </summary>
    /// <returns>An array of <see cref="VertexInputBindingDescription"/> objects.</returns>
    public static VertexInputBindingDescription[] GetBindingDescriptions()
    {
        VertexInputBindingDescription[] bindingDescriptions =
        [
            new VertexInputBindingDescription()
            {
                Binding = 0,
                Stride = SizeOf(),
                InputRate = VertexInputRate.Vertex,
            }
        ];

        return bindingDescriptions;
    }

    /// <summary>
    /// Gets an array of vertex input attribute descriptions for this vertex format.
    /// </summary>
    /// <returns>An array of <see cref="VertexInputAttributeDescription"/> objects.</returns>
    public static VertexInputAttributeDescription[] GetAttributeDescriptions()
    {
        return [
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 0,
                Format = Format.R32G32B32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Position)),
            },
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 1,
                Format = Format.R32G32B32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Color)),
            },
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 2,
                Format = Format.R32G32B32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Normal)),
            },
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 3,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Uv)),
            },
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 4,
                Format = Format.R32G32B32A32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Tangent)),
            }
        ];
    }
}
