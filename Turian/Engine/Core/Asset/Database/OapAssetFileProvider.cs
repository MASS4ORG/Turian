namespace Turian.Engine.Core;

/// <summary>
/// Provides read access to asset content stored inside an Open Asset Package
/// (<c>.oap</c>), including any overlay packages mounted on top of it. The asset is
/// located by the <see cref="Guid"/> encoded in its content key, falling back to the
/// content key as a virtual path.
/// </summary>
/// <param name="oapFilePath">The absolute path to the base <c>.oap</c> file.</param>
/// <param name="contentKey">The logical content key, typically <c>{assetId:N}:primary</c>.</param>
public sealed class OapAssetFileProvider(string oapFilePath, string contentKey) : IAssetFileProvider
{
    /// <summary>Gets the absolute path to the base package file.</summary>
    public string OapFilePath { get; } = oapFilePath;

    /// <inheritdoc/>
    public string ContentKey { get; } = contentKey;

    /// <inheritdoc/>
    public AssetStorageKind StorageKind => AssetStorageKind.Oap;

    /// <inheritdoc/>
    public bool Exists => TryResolve(out _, out _);

    /// <inheritdoc/>
    public long? Length => TryResolve(out _, out var entry) ? (long)entry.UncompressedSize : null;

    /// <inheritdoc/>
    public Stream GetAssetStream()
    {
        if (!TryResolve(out var reader, out var entry))
        {
            throw new FileNotFoundException("OAP asset entry was not found.", ContentKey);
        }

        return reader.OpenAssetStream(entry);
    }

    bool TryResolve(out OapReader reader, out OapIndexEntry entry)
    {
        reader = null!;
        entry = default;

        var mountSet = OapMountSet.ForBasePackage(OapFilePath);
        if (mountSet is null)
        {
            return false;
        }

        if (TryParseAssetId(ContentKey, out var id) && mountSet.TryResolveById(id, out reader, out entry))
        {
            return true;
        }

        return mountSet.TryResolveByPath(ContentKey, out reader, out entry);
    }

    static bool TryParseAssetId(string key, out Guid id)
    {
        var separator = key.IndexOf(':', StringComparison.Ordinal);
        var candidate = separator >= 0 ? key[..separator] : key;
        return Guid.TryParseExact(candidate, "N", out id) || Guid.TryParse(candidate, out id);
    }
}
