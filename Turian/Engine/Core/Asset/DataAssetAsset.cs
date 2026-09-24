namespace Turian.Engine.Core;

/// <summary>
/// Asset metadata that points to a serialized <see cref="DataAsset"/> payload file.
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000006")]
public class DataAssetAsset : Asset
{
    /// <summary>
    /// Loads the payload associated with this metadata asset.
    /// </summary>
    /// <param name="projectPath">The absolute project root path.</param>
    /// <returns>The deserialized <see cref="DataAsset"/> payload instance.</returns>
    public DataAsset? GetContent(string projectPath)
    {
        var sourcePath = Path.Combine(projectPath, RelativePath);
        if (File.Exists(sourcePath)
            || !AssetDatabase.TryGetInstance(out var database)
            || database is null
            || !database.TryGetAssetProvider(Id, out var provider)
            || provider is null)
        {
            return DataAsset.LoadContent(sourcePath);
        }

        // A built or play-mode game has no sources, only the imported copy the catalog points at.
        using var reader = new StreamReader(provider.GetAssetStream());
        return Serializer.LoadData<DataAsset>(reader.ReadToEnd());
    }

    /// <summary>
    /// Loads a payload from the specified absolute path.
    /// </summary>
    /// <param name="absolutePath">The absolute file path of the payload asset.</param>
    /// <returns>The deserialized <see cref="DataAsset"/> payload instance.</returns>
    public static DataAsset? LoadContent(string absolutePath)
    {
        return DataAsset.LoadContent(absolutePath);
    }
}
