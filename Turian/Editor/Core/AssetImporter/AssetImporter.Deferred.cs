namespace Turian.Editor.Core;

public sealed partial class AssetImporter
{
    // Source files skipped because their meta names a type nothing has registered yet.
    readonly HashSet<string> deferredImports = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a failure means the meta's type cannot be resolved or loaded right now, as opposed to the meta being
    /// malformed: an unknown type id, or an assembly, type or initializer that fails to load, anywhere in the chain.
    /// </summary>
    /// <param name="exception">What reading or importing the asset threw.</param>
    /// <returns>True when the meta must be kept as it is.</returns>
    internal static bool IsUnavailableType(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnresolvableTypeIdException or TypeInitializationException or TypeLoadException
                or FileLoadException or BadImageFormatException or ReflectionTypeLoadException
                || current is FileNotFoundException { FileName: { } named } && !Path.IsPathRooted(named))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Imports again the assets that were skipped because the type their meta names was not registered yet. Call it
    /// after a compile that may register that type; assets whose type is still missing stay deferred.
    /// </summary>
    /// <returns>The number of assets imported.</returns>
    public int ImportDeferredAssets()
    {
        lock (syncRoot)
        {
            if (disposed || deferredImports.Count == 0 || assetsRootPath is null)
            {
                return 0;
            }

            RefreshImporters();
            var files = deferredImports.Where(File.Exists).Order(StringComparer.Ordinal).ToArray();
            deferredImports.Clear();
            var batch = new List<(Asset Asset, string SourcePath)>();
            pendingBatch = batch;
            try
            {
                ImportFiles(files, NullProgressSink.Instance);
                IndexImportedAssets(batch, NullProgressSink.Instance);
            }
            finally
            {
                pendingBatch = null;
            }

            PersistCacheCatalog();
            logger.LogInformation("Imported {Count} asset(s) that were waiting for their type; {Remaining} still wait",
                files.Length - deferredImports.Count, deferredImports.Count);
            NotifyAssetsChanged();
            foreach (var (asset, _) in batch)
            {
                assetDatabase.NotifyAssetReimported(asset.Id);
            }

            return files.Length - deferredImports.Count;
        }
    }

    void ImportDeferredAfterCompile() => _ = ImportDeferredAssets();
}
