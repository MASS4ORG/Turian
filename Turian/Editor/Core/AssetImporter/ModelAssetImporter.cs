namespace Turian.Editor.Core;

/// <summary>
/// Import 3d model assets with default model-specific import settings.
/// </summary>
public class ModelAssetImporter : IAssetImporter
{
    static readonly string[] supportedExtensions =
    [
        ".obj",
        ".dae",
        ".3ds",
        ".blend",
        ".stl",
    ];

    /// <inheritdoc/>
    /// <remarks>Version 2 bakes <c>.obj</c> geometry into <c>.ammesh</c>.</remarks>
    public int Version => 2;

    /// <inheritdoc/>
    public bool IsValid(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        return supportedExtensions.Contains(extension, StringComparer.InvariantCultureIgnoreCase);
    }

    /// <inheritdoc/>
    public object? ImportSettingsFor(Asset asset) => (asset as ModelImportAsset)?.ImportSettings;

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath)
    {
        return new ModelImportAsset
        {
            RelativePath = filePath,
            ImportSettings = new ModelImportSettings()
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Bakes <c>.obj</c> geometry into an <c>.ammesh</c> blob. The remaining extensions are
    /// copied through unchanged.
    /// </remarks>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        if (!Path.GetExtension(sourcePath).Equals(".obj", StringComparison.OrdinalIgnoreCase))
        {
            return IAssetImporter.CopySourceToCache(sourcePath, importDirectory);
        }

        var builder = ObjModelBuilder.Load(sourcePath);

        var blobFileName = $"{IAssetImporter.PrimaryArtifactName}{MeshBlob.FileExtension}";
        MeshBlobWriter.Save(
            Path.Combine(importDirectory, blobFileName),
            MeshBlobBaker.FromModelBuilder(builder, Path.GetFileNameWithoutExtension(sourcePath)));

        return [blobFileName];
    }
}
