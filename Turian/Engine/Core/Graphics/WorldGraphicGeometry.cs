namespace Turian.Engine.Core;

/// <summary>A world-space graphic vertex with linear RGBA color and texture coordinates.</summary>
/// <param name="Position">World position of the vertex.</param>
/// <param name="Color">Linear RGBA tint multiplied by the sampled texture.</param>
/// <param name="Uv">Texture coordinates with a top-left origin for text.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct WorldGraphicVertex(Vector3 Position, Vector4 Color, Vector2 Uv);

/// <summary>CPU geometry consumed by world-space line, trail and text renderers.</summary>
/// <param name="Vertices">World-coordinate graphic vertices.</param>
/// <param name="Indices">Triangle indices into the vertex array.</param>
public sealed record WorldGraphicGeometry(WorldGraphicVertex[] Vertices, uint[] Indices)
{
    /// <summary>Empty geometry shared by invisible or underspecified graphics.</summary>
    public static WorldGraphicGeometry Empty { get; } = new([], []);
}
