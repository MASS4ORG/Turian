namespace Turian.Editor.Core;

public sealed partial class AssetImporter
{
    static Dictionary<string, string> MapTargetArtifacts(
        IAssetImporter? importer,
        IReadOnlyList<string> artifacts)
    {
        var targets = importer?.BuildTargets ?? [];
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < targets.Count && i < artifacts.Count; i++)
        {
            mapped[targets[i]] = artifacts[i];
        }

        return mapped;
    }

    /// <summary>
    /// Emits the child assets a source file declares — one <see cref="MaterialAsset"/> per material
    /// and one <see cref="TextureAsset"/> per image, for formats that have them — serializing each
    /// into the parent's import directory and registering it in the asset database.
    /// </summary>
    /// <remarks>
    /// Child ids are derived from the parent via <see cref="AssetIdFactory.Derive"/>, so they are
    /// stable across reimports and a scene's references survive a re-export of the source model.
    /// Children that no longer exist in the source — a material the artist deleted — are pruned.
    /// Must run after <see cref="ImportAssetToCache"/>, which clears the import directory.
    /// </remarks>
    /// <summary>
    /// Registers files a source asset references, so a model's textures resolve to the assets of
    /// the texture files themselves rather than to copies owned by the model.
    /// </summary>
    sealed class ImportContext(AssetImporter importer) : IAssetImportContext
    {
        /// <inheritdoc/>
        public Guid EnsureAsset(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            {
                return Guid.Empty;
            }

            var fullPath = Path.GetFullPath(absolutePath);
            importer.EnsureAssetImported(fullPath, overwriteExisting: false);

            var metaFilePath = GetMetaFilePath(fullPath);
            return File.Exists(metaFilePath) ? Asset.Load(metaFilePath)?.Id ?? Guid.Empty : Guid.Empty;
        }

        /// <inheritdoc/>
        public void ConfigureTexture(string absolutePath, bool isSrgb, bool flipGreenChannel)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            {
                return;
            }

            var fullPath = Path.GetFullPath(absolutePath);
            var metaFilePath = GetMetaFilePath(fullPath);
            if (!File.Exists(metaFilePath) || Asset.Load(metaFilePath) is not TextureAsset texture)
            {
                return;
            }

            if (texture.IsSrgb == isSrgb && texture.FlipGreenChannel == flipGreenChannel)
            {
                return;
            }

            texture.IsSrgb = isSrgb;
            texture.FlipGreenChannel = flipGreenChannel;
            texture.RelativePath = fullPath;
            File.WriteAllText(metaFilePath, SerializeAssetMetadata(texture));
            importer.ImportAssetToCache(texture, fullPath);
        }
    }

    void RegisterChildAssets(Asset asset, string sourceFilePath)
    {
        var importer = assetImporters.FirstOrDefault(candidate => candidate.IsValid(sourceFilePath));
        if (importer is null)
        {
            return;
        }

        List<Asset> children;
        try
        {
            children = [.. importer.CreateChildAssets(asset.Id, sourceFilePath, new ImportContext(this))];
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Importer {ImporterId} failed to enumerate child assets for {SourceFilePath}",
                importer.GetType().Name,
                sourceFilePath);
            return;
        }

        if (children.Count == 0)
        {
            // Nothing to emit, but a previous import may have left children behind.
            assetDatabase.RemoveChildAssets(asset.Id);
            return;
        }

        var childDirectory = Path.Combine(GetAssetImportDirectory(asset.Id), childAssetsDirectoryName);
        Directory.CreateDirectory(childDirectory);

        var registered = new HashSet<Guid>();

        foreach (var child in children)
        {
            if (child.Id == Guid.Empty || !registered.Add(child.Id))
            {
                continue;
            }

            string childPath;

            try
            {
                var binaryContent = importer.CreateChildAssetBinaryContent(asset.Id, child, sourceFilePath);
                if (binaryContent is not null)
                {
                    childPath = Path.Combine(childDirectory, $"{child.Id:N}.bin");
                    File.WriteAllBytes(childPath, binaryContent);
                }
                else
                {
                    childPath = Path.Combine(childDirectory, $"{child.Id:N}.json");
                    File.WriteAllText(
                        childPath,
                        importer.CreateChildAssetContent(asset.Id, child, sourceFilePath) ?? SerializeAssetMetadata(child));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write child asset {ChildAssetId} for {AssetId}", child.Id, asset.Id);
                registered.Remove(child.Id);
                continue;
            }

            if (!assetDatabase.RegisterChildAsset(asset.Id, child, childPath))
            {
                registered.Remove(child.Id);
            }
        }

        var pruned = assetDatabase.RemoveChildAssets(asset.Id, registered);

        logger.LogDebug(
            "Registered {ChildCount} child assets for {AssetId} ({SourceFilePath}); pruned {PrunedCount} stale",
            registered.Count,
            asset.Id,
            sourceFilePath,
            pruned);
    }

}
