namespace Turian.Editor.Core;

public sealed partial class AssetImporter
{
    bool HasCachedIndex(ImportedAssetManifest manifest) =>
        HasCurrentPrimary(manifest) && manifest.IndexedAssetIds is not null && manifest.IndexedAssetIds.All(id =>
            assetDatabase.TryGetAsset(id, out var record) && record is not null && File.Exists(record.ResolveContentPath()));

    bool HasCurrentPrimary(ImportedAssetManifest manifest) =>
        assetDatabase.TryGetAsset(manifest.AssetId, out var record) && record is not null
        && string.Equals(Path.GetFileName(record.ImportedRelativePath), manifest.PrimaryArtifactFileName,
            StringComparison.Ordinal);

    void SaveIndexStamp(Guid assetId)
    {
        var path = Path.Combine(GetAssetImportDirectory(assetId), importManifestFileName);
        if (LoadManifest(path) is not { } manifest)
        {
            return;
        }

        manifest.IndexedAssetIds = [assetId, .. assetDatabase.GetChildAssets(assetId).Select(static record => record.AssetId)];
        Serializer.Save(path, manifest);
    }

    static string SourceHash(ImportedAssetManifest? manifest, FileInfo source) =>
        manifest is not null && manifest.SourceLength == source.Length
                             && manifest.SourceLastWriteTimeUtc == source.LastWriteTimeUtc
            ? manifest.SourceHash : ComputeFileHash(source.FullName);

    static bool CanReuseImport(ImportedAssetManifest? manifest, string sourceHash, string settingsHash,
        int importerVersion, string directory)
    {
        if (manifest is null || manifest.SourceHash != sourceHash || manifest.SettingsHash != settingsHash)
        {
            return false;
        }

        if (manifest.PipelineVersion != pipelineVersion || manifest.ImporterVersion != importerVersion)
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, manifest.PrimaryArtifactFileName))
               && manifest.Artifacts.All(artifact => File.Exists(Path.Combine(directory, artifact)));
    }

    /// <summary>
    /// Fills an asset's import directory from the bake its brick ships, when that bake was made from the same source
    /// with the same settings, importer and pipeline; null when there is no bake or it is stale, which imports instead.
    /// </summary>
    ImportedAssetManifest? MountBake(Guid assetId, string sourceFilePath, string importDirectory, string sourceHash,
        string settingsHash, int importerVersion)
    {
        if (PackageRootOf(sourceFilePath) is not { } package || !bakeFolders.TryGetValue(package.Root, out var bake)
            || BrickBake.BakedImportOf(bake, assetId) is not { } baked)
        {
            return null;
        }

        var manifest = LoadManifest(Path.Combine(baked, importManifestFileName));
        if (!CanReuseImport(manifest, sourceHash, settingsHash, importerVersion, baked)
            || manifest!.AssetId != assetId || manifest.ImporterId != ResolveImporterId(sourceFilePath)
            || !VerifyBake(bake, assetId, sourceFilePath))
        {
            logger.LogWarning("The baked import of {SourcePath} does not match its source, settings or importer; importing it", sourceFilePath);
            return null;
        }

        ClearImportDirectory(importDirectory);
        CopyBakedFiles(baked, importDirectory);
        var source = new FileInfo(sourceFilePath);
        manifest!.SourceRelativePath = ToProjectRelativePath(sourceFilePath);
        manifest.MetaRelativePath = ToProjectRelativePath(GetMetaFilePath(sourceFilePath));
        manifest.SourceLength = source.Length;
        manifest.SourceLastWriteTimeUtc = source.LastWriteTimeUtc;
        manifest.ImportedAtUtc = DateTimeOffset.UtcNow;
        manifest.IndexedAssetIds = null;
        Serializer.Save(Path.Combine(importDirectory, importManifestFileName), manifest);
        mountedBakes++;
        return manifest;
    }

    static bool VerifyBake(string bake, Guid assetId, string sourceFilePath)
    {
        try
        {
            var sync = JsonSerializer.Deserialize<BakeSync>(File.ReadAllText(Path.Combine(bake, BrickBake.SyncFileName)),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var entry = sync?.Entries.SingleOrDefault(e => e.Guid == assetId);
            if (entry is null || entry.ContentHash != "sha256-" + Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(sourceFilePath))))
            {
                return false;
            }

            var expected = Path.GetFullPath(Path.Combine(bake, BrickBake.ImportedFolder, assetId.ToString("N")))
                           + Path.DirectorySeparatorChar;
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in entry.Artifacts)
            {
                var path = Path.GetFullPath(Path.Combine(bake, artifact.File));
                if (!path.StartsWith(expected, StringComparison.Ordinal) || !files.Add(path) || !File.Exists(path)
                    || artifact.Hash != "sha256-" + Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(path))))
                {
                    return false;
                }
            }

            return files.Contains(Path.Combine(expected, importManifestFileName))
                   && files.SetEquals(Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories));
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    static void CopyBakedFiles(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            // Installed bricks are read-only; the cache's copy must stay writable for later reimports.
            var target = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            File.SetAttributes(target, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            CopyBakedFiles(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }

    static void UpdateSourceStamp(ImportedAssetManifest manifest, FileInfo source, string path)
    {
        if (manifest.SourceLength == source.Length && manifest.SourceLastWriteTimeUtc == source.LastWriteTimeUtc)
        {
            return;
        }

        manifest.SourceLength = source.Length;
        manifest.SourceLastWriteTimeUtc = source.LastWriteTimeUtc;
        Serializer.Save(path, manifest);
    }
}
