namespace Turian.Engine.UI;

/// <summary>
/// Asset metadata for a <c>.uss</c> stylesheet. <see cref="GetContent"/> reads the artifact the
/// importer validated and parses it into a Guinevere <see cref="StyleSheet"/>, cached per asset id.
/// </summary>
[TypeId("a3000001-0000-4000-8000-000000000022")]
public sealed class UiStyleSheetAsset : Asset
{
    static readonly ConcurrentDictionary<Guid, StyleSheet> cache = new();

    /// <summary>
    /// Reads this stylesheet's artifact and returns the parsed <see cref="StyleSheet"/>, or
    /// <c>null</c> when the artifact is missing or unreadable. The result is cached.
    /// </summary>
    public StyleSheet? GetContent()
    {
        if (cache.TryGetValue(Id, out var cached)) return cached;

        if (!AssetDatabase.Instance.TryGetAssetProvider(Id, out var provider) || provider is null)
            return null;

        try
        {
            using var stream = provider.GetAssetStream();
            using var reader = new StreamReader(stream);
            var sheet = StyleSheet.Parse(reader.ReadToEnd());
            cache[Id] = sheet;
            return sheet;
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            Log.Logger.LogError(ex, "Failed to load USS asset {AssetId} ({RelativePath})", Id, RelativePath);
            return null;
        }
    }

    /// <summary>Drops the cached stylesheet for <paramref name="assetId"/>.</summary>
    /// <param name="assetId">The stylesheet asset id.</param>
    public static void InvalidateCacheEntry(Guid assetId) => cache.TryRemove(assetId, out _);

    /// <summary>Clears every cached stylesheet. Call on project unload.</summary>
    public static void ClearCache() => cache.Clear();

    /// <summary>Loads and parses a <c>.uss</c> file directly from disk. Used by tooling and tests.</summary>
    /// <param name="absolutePath">Absolute path of the <c>.uss</c> file.</param>
    public static StyleSheet LoadContent(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return StyleSheet.Parse(File.ReadAllText(absolutePath));
    }
}
