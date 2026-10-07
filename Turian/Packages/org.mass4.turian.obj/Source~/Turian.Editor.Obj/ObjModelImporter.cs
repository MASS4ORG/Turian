using Turian.Editor.Core;
using Turian.Engine.Core;

namespace Turian.Editor.Obj;

/// <summary>Imports Wavefront OBJ sources into the runtime's cooked AMMESH mesh container.</summary>
public sealed class ObjModelImporter : IAssetImporter
{
    /// <inheritdoc/>
    public int Version => 3;

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
        var builder = ObjModelBuilder.Load(sourcePath);
        var artifact = $"{IAssetImporter.PrimaryArtifactName}{MeshBlob.FileExtension}";
        MeshBlobWriter.Save(Path.Combine(importDirectory, artifact),
            MeshBlobBaker.FromModelBuilder(builder, Path.GetFileNameWithoutExtension(sourcePath)));
        return [artifact];
    }
}
