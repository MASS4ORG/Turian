namespace Turian.Engine.Core;

/// <summary>
/// Provides read access to asset content stored inside an Open Asset Package
/// (<c>.oap</c>), including any overlay packages mounted on top of it. The asset is
/// located by the <see cref="System.Guid"/> encoded in its content key, falling back to the
/// content key as a virtual path.
/// </summary>
public sealed class OapAssetFileProvider : IAssetFileProvider
{
    readonly IContentDirectory? content;
    readonly OapMountSet? sessionMount;

    /// <summary>Reads an asset from a base package file.</summary>
    /// <param name="oapFilePath">The absolute path to the base <c>.oap</c> file.</param>
    /// <param name="contentKey">The logical content key, typically <c>{assetId:N}:primary</c>.</param>
    /// <param name="sessionMount">A fixed content-session mount, or null for editor live lookup.</param>
    public OapAssetFileProvider(string oapFilePath, string contentKey, OapMountSet? sessionMount = null)
    {
        OapFilePath = oapFilePath;
        ContentKey = contentKey;
        this.sessionMount = sessionMount;
    }

    /// <summary>Reads an asset from a base package inside a content location, such as a built game's APK assets.</summary>
    /// <param name="content">The location holding the base package.</param>
    /// <param name="package">The base package path relative to <paramref name="content"/>.</param>
    /// <param name="contentKey">The logical content key, typically <c>{assetId:N}:primary</c>.</param>
    /// <param name="sessionMount">A fixed content-session mount, or null for live lookup.</param>
    public OapAssetFileProvider(IContentDirectory content, string package, string contentKey,
        OapMountSet? sessionMount = null)
    {
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        OapFilePath = package;
        ContentKey = contentKey;
        this.sessionMount = sessionMount;
    }

    /// <summary>Gets the base package: an absolute file path, or a path relative to the content location.</summary>
    public string OapFilePath { get; }

    /// <inheritdoc/>
    public string ContentKey { get; }

    /// <inheritdoc/>
    public AssetStorageKind StorageKind => AssetStorageKind.Oap;

    /// <inheritdoc/>
    public bool Exists => TryResolve(out _, out _);

    /// <inheritdoc/>
    public long? Length => TryResolve(out _, out var entry) ? (long)entry.UncompressedSize : null;

    /// <inheritdoc/>
    public Stream GetAssetStream()
    {
        sessionMount?.EnsureUnchanged();
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

        var mountSet = sessionMount ?? (content is null
            ? OapMountSet.ForBasePackage(OapFilePath)
            : OapMountSet.ForBasePackage(content, OapFilePath));
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
