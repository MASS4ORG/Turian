namespace Turian.Editor.Core;

/// <summary>
/// Generic fallback importer used when no specialized importer matches the asset.
/// </summary>
[DefaultOption]
public class GenericAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public bool IsValid(string filePath)
    {
        return !string.IsNullOrWhiteSpace(filePath)
            && !filePath.EndsWith(".meta", StringComparison.InvariantCultureIgnoreCase);
    }

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalizedPath = NormalizePath(filePath);

        if (normalizedPath.EndsWith(".prefab", StringComparison.InvariantCultureIgnoreCase))
        {
            return new Prefab
            {
                RelativePath = normalizedPath
            };
        }

        if (IsDataAssetPath(normalizedPath))
        {
            return new DataAssetAsset
            {
                RelativePath = normalizedPath
            };
        }

        return new Asset
        {
            RelativePath = normalizedPath
        };
    }

    /// <inheritdoc/>
    public IdClass? LoadAuthoredContent(Asset asset, string sourcePath) =>
        asset is DataAssetAsset || IsDataAssetPath(sourcePath) ? DataAsset.LoadContent(sourcePath) : null;

    /// <summary>Whether a file is a data asset by its extension — what the New menu writes, or an older one.</summary>
    /// <param name="path">The source file path.</param>
    /// <returns>True for <c>.dataasset</c>, <c>.asset</c> and <c>.data</c>.</returns>
    public static bool IsDataAssetPath(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".DATAASSET" or ".ASSET" or ".DATA";

    static string NormalizePath(string filePath)
    {
        return filePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim();
    }
}
