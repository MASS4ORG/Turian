namespace Turian.Editor.Core;

/// <summary>
/// Importer for top-level <c>.material</c> assets. The file holds a serialized
/// <see cref="MaterialAsset"/> — PBR factors and texture references — in the same shape
/// as the child materials the model importers emit, so both read through one path.
/// </summary>
public class MaterialAssetImporter : IAssetImporter
{
    const string materialExtension = ".material";

    /// <inheritdoc/>
    public bool IsValid(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return string.Equals(Path.GetExtension(filePath), materialExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IdClass? LoadAuthoredContent(Asset asset, string sourcePath) =>
        Serializer.Load<MaterialAsset>(sourcePath);

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath) => new MaterialAsset
    {
        RelativePath = filePath,
    };
}
