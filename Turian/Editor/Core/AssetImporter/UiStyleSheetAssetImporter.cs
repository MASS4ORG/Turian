namespace Turian.Editor.Core;

/// <summary>
/// Importer for <c>.uss</c> stylesheets. Parses the sheet at import time so a malformed selector
/// or rule block fails the import, then stores the source text as the primary artifact for the
/// runtime to re-parse (the parse is cheap and keeps <c>var(--x)</c> and comments intact for
/// hot-reload diffing).
/// </summary>
public sealed class UiStyleSheetAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public bool IsValid(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && Path.GetExtension(filePath).Equals(".uss", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return new UiStyleSheetAsset { RelativePath = filePath };
    }

    /// <summary>
    /// Parses the stylesheet to validate it, then copies the source text as the primary artifact.
    /// A parse failure is logged rather than thrown: the shared import pipeline has no per-asset
    /// isolation, so throwing here would abort the whole project scan. The runtime's
    /// <see cref="UiStyleSheetAsset.GetContent"/> returns <c>null</c> for a sheet that still fails
    /// to parse.
    /// </summary>
    /// <param name="asset">The stylesheet asset metadata.</param>
    /// <param name="sourcePath">Absolute path of the <c>.uss</c> file.</param>
    /// <param name="importDirectory">Absolute path of the asset's import directory.</param>
    /// <returns>The single copied artifact name.</returns>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importDirectory);

        try
        {
            _ = StyleSheet.Parse(File.ReadAllText(sourcePath));
        }
        catch (FormatException ex)
        {
            Log.Logger.LogError(ex, "USS asset {Path} is malformed; it will not apply at runtime", sourcePath);
        }

        return IAssetImporter.CopySourceToCache(sourcePath, importDirectory);
    }
}
