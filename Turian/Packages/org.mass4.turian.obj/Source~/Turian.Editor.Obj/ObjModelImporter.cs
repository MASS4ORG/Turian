using Turian.Editor.Core;
using Turian.Engine.Core;

namespace Turian.Editor.Obj;

/// <summary>Imports Wavefront OBJ sources into glTF 2 through the editor's Assimp converter.</summary>
public sealed class ObjModelImporter : IAssetImporter
{
    /// <inheritdoc/>
    public int Version => 4;

    /// <inheritdoc/>
    public bool IsValid(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && Path.GetExtension(filePath).Equals(".obj", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public object? ImportSettingsFor(Asset asset) => (asset as ModelImportAsset)?.ImportSettings;

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath) => new ModelImportAsset
    {
        RelativePath = filePath,
        ImportSettings = new ModelImportSettings()
    };

    /// <inheritdoc/>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        var artifact = $"{IAssetImporter.PrimaryArtifactName}.glb";
        AssimpModelConverter.ConvertToGlb(sourcePath, Path.Combine(importDirectory, artifact));
        return [artifact];
    }
}
