namespace Turian.Engine.UI;

/// <summary>
/// Asset metadata for a <c>.uss</c> stylesheet. <see cref="GetContent"/> reads the artifact the
/// importer validated and parses it into a Guinevere <see cref="StyleSheet"/>, cached per asset id.
/// </summary>
[TypeId("e33bac6d-8920-535f-9507-bd4c31915c15")]
public sealed class UiStyleSheetAsset : Asset
{
    static readonly ConcurrentDictionary<Guid, StyleSheet> Cache = new();

    /// <summary>How <c>.uss</c> sheets are parsed: their CSS-flavored <c>prop: value;</c> form is still accepted.</summary>
    public static StyleSheetOptions ParseOptions { get; } = new() { AllowCssSyntax = true };

    /// <summary>
    /// Reads this stylesheet's artifact and returns the parsed <see cref="StyleSheet"/>, or
    /// <c>null</c> when the artifact is missing or unreadable. The result is cached.
    /// </summary>
    /// <param name="database">The asset database the asset is registered in.</param>
    public StyleSheet? GetContent(AssetDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (Cache.TryGetValue(Id, out var cached)) return cached;

        if (!database.TryGetAssetProvider(Id, out var provider) || provider is null)
            return null;

        try
        {
            using var stream = provider.GetAssetStream();
            using var reader = new StreamReader(stream);
            var sheet = StyleSheet.Parse(reader.ReadToEnd(), ParseOptions);
            Cache[Id] = sheet;
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
    public static void InvalidateCacheEntry(Guid assetId) => Cache.TryRemove(assetId, out _);

    /// <summary>Clears every cached stylesheet. Call on project unload.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>Loads and parses a <c>.uss</c> file directly from disk. Used by tooling and tests.</summary>
    /// <param name="absolutePath">Absolute path of the <c>.uss</c> file.</param>
    public static StyleSheet LoadContent(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return StyleSheet.Parse(File.ReadAllText(absolutePath), ParseOptions);
    }
}
