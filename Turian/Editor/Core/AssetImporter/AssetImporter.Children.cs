namespace Turian.Editor.Core;

public sealed partial class AssetImporter
{
    /// <summary>
    /// Re-runs component-type indexing for every prefab in the database.
    /// Call this after a user-code assembly is swapped in so that types that were
    /// unresolvable at import time are now indexed correctly.
    /// </summary>
    public void ReIndexAllPrefabs()
    {
        var snapshot = assetDatabase.GetAssetsSnapshot();
        foreach (var record in snapshot)
        {
            if (!IsPrefabTypeName(record.AssetTypeName))
                continue;

            var prefabAsset = new Prefab { Id = record.AssetId };
            RefreshPrefabComponentIndex(prefabAsset, ResolveImportedPrimaryPath(record.AssetId));
        }
    }

    static bool IsPrefabTypeName(string typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (string.Equals(typeName, typeof(Prefab).FullName, StringComparison.Ordinal)
            || typeName.EndsWith(nameof(Prefab), StringComparison.Ordinal));

    void RefreshPrefabComponentIndex(Asset asset, string primaryArtifactPath)
    {
        if (asset is not Prefab)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(primaryArtifactPath) || !File.Exists(primaryArtifactPath))
        {
            return;
        }

        try
        {
            var root = Serializer.Load<Node>(primaryArtifactPath);
            if (root is null)
            {
                assetDatabase.UpdateComponentTypes(asset.Id, []);
                return;
            }

            var types = new HashSet<string>(StringComparer.Ordinal);
            CollectComponentTypes(root, types);
            assetDatabase.UpdateComponentTypes(asset.Id, types);
        }
        catch (UnresolvableTypeIdException ex)
        {
            // Expected during startup: user-code component types aren't loaded until the
            // user assembly compiles. ReIndexAllPrefabs runs after the assembly swap.
            if (string.IsNullOrWhiteSpace(ex.SourcePath) && assetDatabase.TryGetAsset(asset.Id, out var record) && record is not null)
            {
                ex.SourcePath = record.SourceRelativePath;
            }
            logger.LogDebug(
                "Deferring component-type indexing for prefab {AssetId} at {SourcePath}: {Message}",
                asset.Id,
                ex.SourcePath ?? "unknown",
                ex.Message);
        }
        catch (Exception ex)
        {
            var sourcePath = primaryArtifactPath;
            if (assetDatabase.TryGetAsset(asset.Id, out var record) && record is not null)
            {
                sourcePath = record.SourceRelativePath;
            }
            logger.LogWarning(
                ex,
                "Failed to index component types for prefab {AssetId} at {SourcePath}: {Message}",
                asset.Id,
                sourcePath,
                ex.Message);
        }
    }

    static void CollectComponentTypes(Node node, ISet<string> destination)
    {
        foreach (var component in node.Components)
        {
            if (component is MissingComponent)
            {
                continue;
            }

            var fullName = component.GetType().FullName;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                destination.Add(fullName);
            }
        }

        foreach (var child in node.Children)
        {
            CollectComponentTypes(child, destination);
        }
    }

    string ResolveImportedPrimaryPath(Guid assetId)
    {
        if (cacheAssetsRootPath is null)
        {
            return string.Empty;
        }

        var importDirectory = GetAssetImportDirectory(assetId);
        if (!Directory.Exists(importDirectory))
        {
            return string.Empty;
        }

        var manifestPath = Path.Combine(importDirectory, importManifestFileName);
        var manifest = LoadManifest(manifestPath);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.PrimaryArtifactFileName))
        {
            return string.Empty;
        }

        return Path.Combine(importDirectory, manifest.PrimaryArtifactFileName);
    }

    ImportedAssetManifest? LoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return Serializer.Load<ImportedAssetManifest>(manifestPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read import manifest {ManifestPath}", manifestPath);
            return null;
        }
    }

}
