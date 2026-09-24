namespace Turian.Editor.Core;

/// <summary>
/// What the inspector shows for an asset selected in the browser: the asset's metadata, and the
/// object whose members are drawn — the authored content of a data asset or material, or the import
/// settings of the importer that owns the file. <see cref="Target"/> is null for a kind with neither.
/// </summary>
/// <param name="Metadata">The asset metadata read from the <c>.meta</c> file.</param>
/// <param name="AbsolutePath">Absolute path of the source file.</param>
/// <param name="Target">The object the form draws, or null when the kind has nothing to configure.</param>
/// <param name="Title">The heading the inspector draws above it.</param>
/// <param name="IsPayload">
/// True when <paramref name="Target"/> is the file's authored content rather than its import settings.
/// </param>
public sealed record AssetInspection(
    Asset Metadata,
    string AbsolutePath,
    object? Target,
    string Title,
    bool IsPayload);

/// <summary>
/// Resolves what the inspector edits for a file in the asset browser, and writes an edit back. An
/// authored file is its content class, the way a ScriptableObject is edited directly; everything else
/// is the import settings its importer declares, applied by rewriting the <c>.meta</c> and reimporting.
/// Both are asked of the importer that owns the file.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetInspectionService(
    AssetImporter importer,
    AssetTypeCatalog types,
    SettingsService settings,
    ILogger log)
{
    /// <summary>What the inspector should edit for a browser row.</summary>
    /// <param name="entry">The selected row; directories have nothing to inspect.</param>
    /// <returns>The inspection, or null when the row has no asset behind it.</returns>
    public AssetInspection? Inspect(AssetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.IsDirectory ? null : Inspect(entry.AbsolutePath);
    }

    /// <summary>
    /// What the inspector should edit for a source file, read fresh from disk — which is also what
    /// reverting an edit does.
    /// </summary>
    /// <param name="absolutePath">Absolute path of the source file.</param>
    /// <returns>The inspection, or null when the file has no meta file.</returns>
    public AssetInspection? Inspect(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var metaPath = $"{absolutePath}.meta";
        if (!File.Exists(metaPath)) return null;

        var metadata = Load(() => Asset.Load(metaPath), metaPath);
        if (metadata is null) return null;

        var owner = importer.ImporterFor(absolutePath);
        var kind = types.Resolve(absolutePath)?.DisplayName ?? "Asset";

        if (Load(() => owner?.LoadAuthoredContent(metadata, absolutePath), absolutePath) is { } content)
            return new AssetInspection(metadata, absolutePath, content, content.GetType().Name, IsPayload: true);

        var importSettings = owner?.ImportSettingsFor(metadata);

        return new AssetInspection(metadata, absolutePath, importSettings, $"{kind} Import Settings",
            IsPayload: false);
    }

    /// <summary>
    /// Writes an edited inspection back and reimports the asset, so the cache artifact the editor and
    /// a built game read matches what the inspector now shows.
    /// </summary>
    /// <param name="inspection">The inspection whose target was edited.</param>
    /// <returns>True when the file was written.</returns>
    public bool Apply(AssetInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        try
        {
            if (inspection.IsPayload)
            {
                // Serialized through a concrete base: the polymorphic converters are registered per
                // concrete type, so a static type of IdClass would write the members without a type id.
                switch (inspection.Target)
                {
                    case DataAsset payload:
                        Serializer.Save(inspection.AbsolutePath, payload);
                        break;
                    case Asset content:
                        Serializer.Save(inspection.AbsolutePath, content);
                        break;
                    default:
                        return false;
                }
            }
            else
            {
                Serializer.Save($"{inspection.AbsolutePath}.meta", inspection.Metadata);
            }

            importer.ReimportNow(inspection.AbsolutePath);

            // The open project keeps the settings it read at open; one applied here replaces it, so a
            // play session started next reads the edit.
            if (inspection.Target is ProjectSettingsAsset applied
                && settings.Settings is { } project
                && ProjectSettingsFiles.Locate(project, applied.GetType()) == inspection.AbsolutePath)
                project.Loaded.Use(applied);

            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not apply the changes to {Path}", inspection.AbsolutePath);
            return false;
        }
    }

    T? Load<T>(Func<T?> load, string path)
        where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read {Path}", path);
            return null;
        }
    }
}
