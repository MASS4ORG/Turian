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
    bool IsPayload)
{
    internal VariantEdit? Variant { get; set; }
}

sealed record VariantEdit(string Source, string Baseline, Dictionary<Guid, string?> Bases);

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
    IAssetLoader loader,
    ILogger log)
{
    /// <summary>What the inspector should edit for a browser row.</summary>
    /// <param name="entry">The selected row; directories have nothing to inspect.</param>
    /// <returns>The inspection, or null when the row has no asset behind it.</returns>
    public AssetInspection? Inspect(AssetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsDirectory)
        {
            return null;
        }

        var inspection = Inspect(entry.AbsolutePath);
        return entry.IsReadOnly && inspection is not null
            ? inspection with { Target = null, Title = $"{inspection.Title} (read-only brick)" }
            : inspection;
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
        if (!File.Exists(metaPath))
        {
            return null;
        }

        var metadata = Load(() => Asset.Load(metaPath), metaPath);
        if (metadata is null)
        {
            return null;
        }

        var owner = importer.ImporterFor(absolutePath);
        var kind = types.Resolve(absolutePath)?.DisplayName ?? "Asset";

        if (metadata is DataAssetAsset && Load(() => InspectDataAsset(metadata, absolutePath), absolutePath) is { } data)
        {
            return data;
        }

        if (Load(() => owner?.LoadAuthoredContent(metadata, absolutePath), absolutePath) is { } content)
        {
            return new AssetInspection(metadata, absolutePath, content, content.GetType().Name, IsPayload: true);
        }

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
                // concrete type, so a static type of IdObject would write the members without a type id.
                switch (inspection.Target)
                {
                    case DataAsset payload:
                        var errors = DataAssetValidation.Validate(payload, inspection.AbsolutePath);
                        if (errors.Count > 0)
                        {
                            foreach (var error in errors)
                            {
                                log.LogError("{Path}: {Member}: {Message}", error.Path, error.Member, error.Message);
                            }
                            return false;
                        }

                        SaveDataAsset(inspection, payload);
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
            {
                project.Loaded.Use(applied);
            }

            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not apply the changes to {Path}", inspection.AbsolutePath);
            return false;
        }
    }

    AssetInspection? InspectDataAsset(Asset metadata, string path)
    {
        if (File.Exists(path) && File.ReadAllText(path) is { } source && DataAssetVariants.IsVariant(source))
        {
            var bases = new Dictionary<Guid, string?>();
            var variant = DataAsset.LoadPayload(source, metadata.Id, id =>
                bases.TryGetValue(id, out var json) ? json : bases[id] = importer.ReadDataAssetSource(id))
                ?? throw new InvalidDataException("The DataAsset variant could not be loaded.");
            ObjectReferences.ResolveAsync(variant, loader).GetAwaiter().GetResult();
            return new AssetInspection(metadata, path, variant, variant.GetType().Name, IsPayload: true)
            {
                Variant = new(source, Serializer.Serialize(variant), bases)
            };
        }

        var shared = loader.LoadContentAsync<DataAsset>(metadata.Id).GetAwaiter().GetResult();
        return shared is null ? null : new AssetInspection(metadata, path, shared, shared.GetType().Name, IsPayload: true);
    }

    void SaveDataAsset(AssetInspection inspection, DataAsset payload)
    {
        var path = inspection.AbsolutePath;
        var edited = Serializer.Serialize(payload);
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (inspection.Variant is { } variant)
        {
            if (!string.Equals(current, variant.Source, StringComparison.Ordinal)
                || variant.Bases.Any(pair => !string.Equals(
                    importer.ReadDataAssetSource(pair.Key), pair.Value, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Reopen the DataAsset variant because it or one of its bases changed.");
            }

            var updated = DataAssetVariants.UpdateOverrides(variant.Source, variant.Baseline, edited);
            var reopened = DataAsset.LoadPayload(updated, payload.Id, id => variant.Bases.GetValueOrDefault(id));
            if (reopened is null)
            {
                throw new InvalidDataException("The edited DataAsset variant could not be reopened.");
            }
            ObjectReferences.ResolveAsync(reopened, loader).GetAwaiter().GetResult();
            if (!JsonNode.DeepEquals(JsonNode.Parse(edited), JsonNode.Parse(Serializer.Serialize(reopened))))
            {
                throw new InvalidOperationException("The DataAsset variant overrides cannot preserve the edited values.");
            }

            AtomicFile.WriteAllText(path, updated);
            inspection.Variant = variant with { Source = updated, Baseline = edited };
            return;
        }

        if (current is not null && DataAssetVariants.IsVariant(current))
        {
            throw new InvalidOperationException("Open the DataAsset variant to edit its overrides.");
        }

        AtomicFile.WriteAllText(path, edited);
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
