namespace Turian.Engine.Core;

/// <summary>
/// A range of submeshes inside a <see cref="ModelAsset"/>, produced as a child asset
/// by the model importers — one per mesh-bearing node of the source file. Every mesh
/// of one file shares that file's vertex and index buffers.
/// </summary>
[TypeId("a3000000-0000-4000-8000-00000000000c")]
public class MeshAsset : Asset
{
    /// <summary>Gets or sets the model asset that owns the shared buffers.</summary>
    public AssetReference<ModelAsset>? Model { get; set; }

    /// <summary>Gets or sets the index of the first submesh this mesh draws.</summary>
    public uint SubMeshStart { get; set; }

    /// <summary>Gets or sets the number of consecutive submeshes this mesh draws.</summary>
    public uint SubMeshCount { get; set; }

    /// <summary>Gets or sets the axis-aligned bounds of the mesh in model space.</summary>
    public Bounds Bounds { get; set; }

    /// <summary>
    /// Reads the mesh metadata registered under <paramref name="assetId"/> from the asset database.
    /// </summary>
    /// <param name="assetId">Identifier of the mesh child asset.</param>
    /// <returns>The mesh asset, or <c>null</c> when it cannot be resolved.</returns>
    public static MeshAsset? Resolve(Guid assetId)
    {
        if (assetId == Guid.Empty
            || !AssetDatabase.Instance.TryGetAssetProvider(assetId, out var provider)
            || provider is null)
        {
            return null;
        }

        try
        {
            using var stream = provider.GetAssetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Serializer.LoadData<MeshAsset>(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to read mesh asset {AssetId}", assetId);
            return null;
        }
    }
}
