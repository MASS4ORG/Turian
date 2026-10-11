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
        if (LoadManifest(path) is not { } manifest) return;
        manifest.IndexedAssetIds = [assetId, .. assetDatabase.GetChildAssets(assetId).Select(record => record.AssetId)];
        Serializer.Save(path, manifest);
    }

    static string SourceHash(ImportedAssetManifest? manifest, FileInfo source) =>
        manifest is not null && manifest.SourceLength == source.Length
                             && manifest.SourceLastWriteTimeUtc == source.LastWriteTimeUtc
            ? manifest.SourceHash : ComputeFileHash(source.FullName);

    static bool CanReuseImport(ImportedAssetManifest? manifest, string sourceHash, string settingsHash,
        int importerVersion, string directory)
    {
        if (manifest is null || manifest.SourceHash != sourceHash || manifest.SettingsHash != settingsHash) return false;
        if (manifest.PipelineVersion != pipelineVersion || manifest.ImporterVersion != importerVersion) return false;
        return File.Exists(Path.Combine(directory, manifest.PrimaryArtifactFileName))
               && manifest.Artifacts.All(artifact => File.Exists(Path.Combine(directory, artifact)));
    }

    static void UpdateSourceStamp(ImportedAssetManifest manifest, FileInfo source, string path)
    {
        if (manifest.SourceLength == source.Length && manifest.SourceLastWriteTimeUtc == source.LastWriteTimeUtc) return;
        manifest.SourceLength = source.Length;
        manifest.SourceLastWriteTimeUtc = source.LastWriteTimeUtc;
        Serializer.Save(path, manifest);
    }
}
