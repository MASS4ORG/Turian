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
}
