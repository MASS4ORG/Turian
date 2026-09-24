namespace Turian.Editor.Core;

/// <summary>
/// Importer for UI fonts (<c>.ttf</c> / <c>.otf</c>). Validates the file is a readable typeface at
/// import time — a corrupt font is logged, not thrown, since the shared pipeline has no per-asset
/// isolation — and stores the source file as the primary artifact for the runtime to load.
/// </summary>
public sealed class UiFontAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public bool IsValid(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return new UiFontAsset { RelativePath = filePath };
    }

    /// <summary>Validates the typeface, then copies the source file as the primary artifact.</summary>
    /// <param name="asset">The font asset metadata.</param>
    /// <param name="sourcePath">Absolute path of the font file.</param>
    /// <param name="importDirectory">Absolute path of the asset's import directory.</param>
    /// <returns>The single copied artifact name.</returns>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importDirectory);

        using var typeface = SKTypeface.FromFile(sourcePath);
        if (typeface is null)
            Log.Logger.LogError("UI font {Path} is not a readable typeface; it will fall back at runtime", sourcePath);

        return IAssetImporter.CopySourceToCache(sourcePath, importDirectory);
    }
}
