namespace Turian.Engine.Core;

/// <summary>
/// Plain mesh data — vertices, indices and submeshes — assembled by an importer or loader and
/// consumed by <see cref="Model"/>. Carries no file-format knowledge of its own.
/// </summary>
public struct ModelBuilder
{
    /// <summary>
    /// Gets or sets the array of vertices for the model.
    /// </summary>
    public Vertex[] Vertices { get; set; }

    /// <summary>
    /// Gets or sets the array of indices for the model.
    /// </summary>
    public uint[] Indices { get; set; }

    /// <summary>
    /// Gets or sets the submeshes describing index ranges and material bindings.
    /// When empty, <see cref="Model"/> creates a single submesh spanning all indices.
    /// </summary>
    public List<SubMesh> SubMeshes { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelBuilder"/> struct.
    /// </summary>
    public ModelBuilder()
    {
        Vertices = [];
        Indices = [];
        SubMeshes = [];
    }

    /// <summary>Resolves submeshes, computing bounds for procedural geometry that supplies no bounds.</summary>
    internal IReadOnlyList<SubMesh> GetBoundedSubMeshes()
    {
        var source = SubMeshes is { Count: > 0 }
            ? SubMeshes
            : [new SubMesh(0, (uint)(Indices.Length > 0 ? Indices.Length : Vertices.Length))];
        var result = new SubMesh[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var sub = source[i];
            result[i] = sub.Bounds == default || sub.Bounds.IsEmpty
                ? sub with { Bounds = ComputeBounds(sub) }
                : sub;
        }

        return Array.AsReadOnly(result);
    }

    Bounds ComputeBounds(SubMesh sub)
    {
        var bounds = Bounds.Empty;
        var indexed = Indices.Length > 0;
        var last = Math.Min((ulong)sub.IndexStart + sub.IndexCount,
            (ulong)(indexed ? Indices.Length : Vertices.Length));
        for (var i = sub.IndexStart; i < last; i++)
        {
            var index = indexed ? Indices[i] : i;
            bounds = bounds.Encapsulate(Vertices[index].Position);
        }

        return bounds;
    }
}
