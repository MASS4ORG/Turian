namespace Turian.Editor.Core;

/// <summary>Validates and normalizes JSON locale tables into cache artifacts.</summary>
/// <remarks>
/// The source <c>.strings</c> file is already the runtime JSON shape, so importing mostly validates
/// it and re-bakes it as the primary artifact. Round-tripping through <see cref="StringTableJson"/>
/// rejects malformed JSON at compile time rather than at runtime.
/// </remarks>
public sealed class StringTableAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public bool IsValid(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && Path.GetExtension(filePath).Equals(".strings", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath) =>
        new StringTableAsset { RelativePath = filePath };

    /// <inheritdoc/>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var table = StringTableJson.Load(File.ReadAllText(sourcePath));
        var artifact = $"{IAssetImporter.PrimaryArtifactName}.strings";
        File.WriteAllText(Path.Combine(importDirectory, artifact), StringTableJson.Save(table));
        return [artifact];
    }
}
