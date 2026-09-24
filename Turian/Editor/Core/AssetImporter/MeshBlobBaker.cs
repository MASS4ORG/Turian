namespace Turian.Editor.Core;

/// <summary>
/// Turns geometry loaded by the engine's model readers into <see cref="MeshBlobContent"/>.
/// </summary>
public static class MeshBlobBaker
{
    /// <summary>
    /// Bakes a <see cref="ModelBuilder"/> into a single-mesh blob whose one mesh entry spans
    /// every submesh, and computes the submesh, mesh and file bounds from the vertices.
    /// </summary>
    /// <param name="builder">The loaded geometry.</param>
    /// <param name="meshName">Name given to the single mesh entry.</param>
    public static MeshBlobContent FromModelBuilder(ModelBuilder builder, string meshName)
    {
        var vertices = builder.Vertices ?? [];
        var indices = builder.Indices ?? [];

        var subMeshes = builder.SubMeshes is { Count: > 0 }
            ? builder.SubMeshes
            : [new SubMesh(0, (uint)indices.Length)];

        var bounded = subMeshes
            .Select(subMesh => subMesh with { Bounds = ComputeBounds(vertices, indices, subMesh) })
            .ToList();

        var meshBounds = bounded.Aggregate(Bounds.Empty, static (acc, subMesh) => acc.Encapsulate(subMesh.Bounds));

        return new MeshBlobContent
        {
            Vertices = vertices,
            Indices = indices,
            SubMeshes = bounded,
            Meshes = [new MeshBlobMesh(meshName, 0, (uint)bounded.Count, meshBounds)],
            Bounds = meshBounds,
        };
    }

    /// <summary>
    /// Computes the axis-aligned bounds of the vertices a submesh's index range addresses.
    /// </summary>
    /// <param name="vertices">The shared vertex buffer.</param>
    /// <param name="indices">The shared index buffer.</param>
    /// <param name="subMesh">The submesh whose range is measured.</param>
    public static Bounds ComputeBounds(Vertex[] vertices, uint[] indices, SubMesh subMesh)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(subMesh);

        var bounds = Bounds.Empty;
        var last = Math.Min(subMesh.IndexStart + subMesh.IndexCount, (uint)indices.Length);

        for (var i = subMesh.IndexStart; i < last; i++)
        {
            var vertexIndex = indices[i];
            if (vertexIndex < vertices.Length)
            {
                bounds = bounds.Encapsulate(vertices[vertexIndex].Position);
            }
        }

        return bounds.IsEmpty ? new Bounds(Vector3.Zero, Vector3.Zero) : bounds;
    }
}
