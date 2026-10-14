namespace Turian.Engine.Core;

/// <summary>
/// Represents a model asset, which can be used to load a 3D model from resolved asset content.
/// </summary>
[TypeId("ddba9f04-28e9-58ba-8043-cde3e44bdbb1")]
public class ModelAsset : Asset
{
    /// <summary>
    /// Reads this model's glTF 2 geometry and registered external buffers into memory, without a GPU.
    /// </summary>
    /// <param name="database">The asset database the model is registered in.</param>
    /// <returns>The geometry, or <c>null</c> when the asset is not registered.</returns>
    /// <exception cref="InvalidDataException">The geometry or one of its buffers is missing or malformed.</exception>
    public ModelBuilder? ReadGeometry(AssetDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!database.TryGetAssetProvider(Id, out var provider) || provider is null)
        {
            return null;
        }

        using var assetStream = provider.GetAssetStream();
        return GltfModelReader.Read(assetStream,
            (index, _) => ReadBuffer(database, AssetIdFactory.Derive(Id, $"buffer:{index}")));
    }

    static byte[] ReadBuffer(AssetDatabase database, Guid id)
    {
        if (!database.TryGetAssetProvider(id, out var provider) || provider is null)
        {
            throw new InvalidDataException($"Missing glTF buffer asset {id}.");
        }

        using var stream = provider.GetAssetStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static string ResolveProviderPath(IAssetFileProvider provider) => provider switch
    {
        RealFileSystemProvider real => real.FilePath,
        LooseFileAssetProvider loose => loose.FilePath,
        _ => provider.GetType().Name,
    };
}
