namespace Turian.Editor.Core;

/// <summary>
/// The geometry a single source file bakes into one <c>.ammesh</c> container.
/// All submeshes address <see cref="Indices"/>, and all indices address <see cref="Vertices"/>.
/// </summary>
public sealed record MeshBlobContent
{
    /// <summary>The interleaved vertex stream.</summary>
    public Vertex[] Vertices { get; init; } = [];

    /// <summary>The secondary UV set, empty when the source has none.</summary>
    public Vector2[] TexCoord1 { get; init; } = [];

    /// <summary>The shared index buffer.</summary>
    public uint[] Indices { get; init; } = [];

    /// <summary>The submesh table.</summary>
    public IReadOnlyList<SubMesh> SubMeshes { get; init; } = [];

    /// <summary>The mesh table; one entry per <see cref="MeshAsset"/> the file produces.</summary>
    public IReadOnlyList<MeshBlobMesh> Meshes { get; init; } = [];

    /// <summary>Axis-aligned bounds covering every mesh.</summary>
    public Bounds Bounds { get; init; }
}
