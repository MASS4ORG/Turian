namespace Turian.Engine.Core;

/// <summary>
/// Asset metadata for a <c>.strings</c> localization table. The imported runtime form is JSON —
/// <see cref="StringTableJson"/> — regardless of whether the source was authored directly in JSON or
/// exported from XLIFF/CSV. <see cref="GetContent"/> reads it through the asset provider, like
/// <c>Turian.Engine.UI.Assets.UiDocumentAsset</c>, so it works for loose editors and packed builds
/// alike.
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000031")]
public sealed class StringTableAsset : Asset
{
    static readonly ConcurrentDictionary<Guid, StringTable> cache = new();

    /// <summary>
    /// Reads this table and returns the parsed <see cref="StringTable"/>, or <c>null</c> when the
    /// artifact is missing or unreadable. The result is cached — callers must not mutate it.
    /// </summary>
    public StringTable? GetContent()
    {
        if (cache.TryGetValue(Id, out var cached)) return cached;

        if (!AssetDatabase.Instance.TryGetAssetProvider(Id, out var provider) || provider is null)
            return null;

        try
        {
            using var stream = provider.GetAssetStream();
            using var reader = new StreamReader(stream);
            var table = StringTableJson.Load(reader.ReadToEnd());
            cache[Id] = table;
            return table;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Logger.LogError(ex, "Failed to load string table asset {AssetId} ({RelativePath})", Id, RelativePath);
            return null;
        }
    }

    /// <summary>Drops the cached table for <paramref name="assetId"/>, forcing a reload on next access.</summary>
    /// <param name="assetId">The string table asset id.</param>
    public static void InvalidateCacheEntry(Guid assetId) => cache.TryRemove(assetId, out _);

    /// <summary>Clears every cached table. Call on project unload.</summary>
    public static void ClearCache() => cache.Clear();

    /// <summary>Loads a <c>.strings</c> file directly from disk, bypassing the asset database.</summary>
    /// <param name="absolutePath">Absolute path of the <c>.strings</c> file.</param>
    /// <returns>The parsed table.</returns>
    public static StringTable LoadContent(string absolutePath) => StringTableJson.Load(File.ReadAllText(absolutePath));
}
