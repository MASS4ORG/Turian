namespace Turian.Engine.Core;

/// <summary>
/// One entry of a mesh blob's mesh table. Each entry becomes a <see cref="MeshAsset"/>.
/// </summary>
/// <param name="Name">Name of the source node the submeshes came from.</param>
/// <param name="SubMeshStart">Index of the first submesh belonging to this mesh.</param>
/// <param name="SubMeshCount">Number of consecutive submeshes belonging to this mesh.</param>
/// <param name="Bounds">Axis-aligned bounds covering the mesh's submeshes.</param>
public sealed record MeshBlobMesh(string Name, uint SubMeshStart, uint SubMeshCount, Bounds Bounds);
