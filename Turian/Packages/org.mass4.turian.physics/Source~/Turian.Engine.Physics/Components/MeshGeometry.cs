namespace Turian.Engine.Physics;

/// <summary>A mesh collider's CPU geometry: vertex positions and three indices per triangle.</summary>
/// <param name="Vertices">The vertex positions in the node's local space.</param>
/// <param name="Indices">The triangle indices into <paramref name="Vertices"/>.</param>
sealed record MeshGeometry(Vector3[] Vertices, int[] Indices)
{
    /// <summary>
    /// Reads a model's geometry from its glTF, keeping only the submeshes of <paramref name="meshId"/> when given, with
    /// unused vertices dropped.
    /// </summary>
    /// <returns>The geometry, or null when nothing can be read.</returns>
    public static MeshGeometry? Read(AssetDatabase database, Guid modelId, Guid meshId)
    {
        var range = (Start: 0u, Count: uint.MaxValue);
        if (meshId != Guid.Empty)
        {
            if (MeshAsset.Resolve(database, meshId) is not { } mesh)
            {
                return null;
            }

            modelId = mesh.Model?.AssetId ?? modelId;
            range = (mesh.SubMeshStart, mesh.SubMeshCount);
        }

        if (modelId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return new ModelAsset { Id = modelId }.ReadGeometry(database) is { } builder ? Extract(builder, range) : null;
        }
        catch (InvalidDataException ex)
        {
            Log.Logger.LogWarning(ex, "Mesh collider could not read model {ModelId}", modelId);
            return null;
        }
    }

    /// <summary>The same triangles with the opposite winding.</summary>
    public static int[] Flip(int[] indices)
    {
        var flipped = (int[])indices.Clone();
        for (var i = 0; i + 2 < flipped.Length; i += 3)
        {
            (flipped[i + 1], flipped[i + 2]) = (flipped[i + 2], flipped[i + 1]);
        }

        return flipped;
    }

    static MeshGeometry? Extract(ModelBuilder builder, (uint Start, uint Count) range)
    {
        var subMeshes = builder.SubMeshes is { Count: > 0 }
            ? builder.SubMeshes
            : [new SubMesh(0, (uint)builder.Indices.Length)];
        var remap = new Dictionary<uint, int>();
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var last = Math.Min((long)range.Start + range.Count, subMeshes.Count);
        for (var s = (int)range.Start; s < last; s++)
        {
            var sub = subMeshes[s];
            var end = Math.Min((long)sub.IndexStart + sub.IndexCount, builder.Indices.Length);
            for (var i = sub.IndexStart; i < end; i++)
            {
                var source = builder.Indices[i];
                if (!remap.TryGetValue(source, out var index))
                {
                    index = vertices.Count;
                    remap[source] = index;
                    vertices.Add(builder.Vertices[source].Position);
                }

                indices.Add(index);
            }
        }

        return vertices.Count == 0 ? null : new MeshGeometry([.. vertices], [.. indices]);
    }
}
