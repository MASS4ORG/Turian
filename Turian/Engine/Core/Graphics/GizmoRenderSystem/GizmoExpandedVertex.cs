namespace Turian.Engine.Core;

/// <summary>
/// A GPU-side vertex produced by expanding a <see cref="GizmoLine"/> into its quad corners.
/// Each line maps to six vertices (two triangles): four outer corners plus two duplicated end vertices
/// that carry the capped-end expansion data.
/// </summary>
public readonly record struct GizmoExpandedVertex(
    Vector3 A,
    Vector3 B,
    Vector4 Color,
    float Thickness,
    float Side,
    float End)
{
    /// <summary>Byte size of this vertex, matching the vertex input layout in <c>gizmoShader.vert</c>.</summary>
    public static int SizeOf() => (3 * 4) + (3 * 4) + (4 * 4) + 4 + 4 + 4;
}
