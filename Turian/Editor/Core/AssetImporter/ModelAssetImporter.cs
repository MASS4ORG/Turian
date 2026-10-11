namespace Turian.Editor.Core;

/// <summary>
/// Import 3d model assets with default model-specific import settings.
/// </summary>
[DefaultOption]
public class ModelAssetImporter : IAssetImporter
{
    static readonly string[] SupportedExtensions =
    [
        ".obj",
        ".dae",
        ".3ds",
        ".stl",
    ];

    /// <inheritdoc/>
    public int Version => 4;

    /// <inheritdoc/>
    public bool IsValid(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(extension, StringComparer.InvariantCultureIgnoreCase);
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
    /// <remarks>OBJ requires its editor brick; supported interchange formats export through Assimp.</remarks>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        if (Path.GetExtension(sourcePath).Equals(".obj", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "OBJ import requires the org.mass4.turian.obj editor brick. Install builtin:org.mass4.turian.obj "
                + "or convert the model to glTF/GLB or FBX, preserving its .meta asset ID and scene references.");
        }

        var extension = Path.GetExtension(sourcePath);
        if (extension.Equals(".max", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".blend", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Export native authoring files to glTF/GLB or FBX before importing.");
        var artifact = $"{IAssetImporter.PrimaryArtifactName}.glb";
        AssimpModelConverter.ConvertToGlb(sourcePath, Path.Combine(importDirectory, artifact));
        return [artifact];
    }
}
