namespace Turian.Engine.UI;

/// <summary>
/// Asset metadata for a UI font (<c>.ttf</c> / <c>.otf</c>). <see cref="GetContent"/> reads the
/// imported file and loads it as a Guinevere <see cref="Font"/> at a default size; callers set the
/// point size with <see cref="Font.WithSize"/>. Loaded faces are cached per asset id.
/// </summary>
[TypeId("8fb85601-cb69-5917-9351-91dea84247b0")]
public sealed class UiFontAsset : Asset
{
    static readonly ConcurrentDictionary<Guid, Font> Cache = new();

    /// <summary>Reads this font's artifact and returns a base <see cref="Font"/>, or <c>null</c>.</summary>
    public Font? GetContent()
    {
        if (Cache.TryGetValue(Id, out var cached)) return cached;

        if (!AssetDatabase.Instance.TryGetAssetProvider(Id, out var provider) || provider is null)
            return null;

        try
        {
            using var stream = provider.GetAssetStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            var font = Font.FromStream(buffer);
            Cache[Id] = font;
            return font;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            Log.Logger.LogError(ex, "Failed to load UI font asset {AssetId} ({RelativePath})", Id, RelativePath);
            return null;
        }
    }

    /// <summary>Drops the cached font for <paramref name="assetId"/>.</summary>
    /// <param name="assetId">The font asset id.</param>
    public static void InvalidateCacheEntry(Guid assetId) => Cache.TryRemove(assetId, out _);

    /// <summary>Clears every cached font. Call on project unload.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>Loads a font file directly from disk. Used by tooling and tests.</summary>
    /// <param name="absolutePath">Absolute path of the <c>.ttf</c> / <c>.otf</c> file.</param>
    public static Font LoadContent(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return Font.FromFile(absolutePath);
    }
}
