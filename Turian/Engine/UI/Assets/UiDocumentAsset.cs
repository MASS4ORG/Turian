namespace Turian.Engine.UI;

/// <summary>
/// Asset metadata for a <c>.ui</c> document. <see cref="GetContent"/> reads the artifact the
/// importer baked — normalised <see cref="UiDocument"/> JSON — and deserialises it, skipping the
/// XML parse at runtime. The parsed document is cached per asset id.
/// </summary>
[TypeId("a3000001-0000-4000-8000-000000000021")]
public sealed class UiDocumentAsset : Asset
{
    static readonly ConcurrentDictionary<Guid, UiDocument> cache = new();

    /// <summary>
    /// Reads this document's baked artifact and returns the parsed <see cref="UiDocument"/>, or
    /// <c>null</c> when the artifact is missing or unreadable. The result is cached — callers must
    /// not mutate it.
    /// </summary>
    public UiDocument? GetContent()
    {
        if (cache.TryGetValue(Id, out var cached)) return cached;

        if (!AssetDatabase.Instance.TryGetAssetProvider(Id, out var provider) || provider is null)
            return null;

        try
        {
            using var stream = provider.GetAssetStream();
            using var reader = new StreamReader(stream);
            var document = ParseText(reader.ReadToEnd(), RelativePath);
            cache[Id] = document;
            return document;
        }
        catch (Exception ex) when (ex is IOException or UiParseException or JsonException)
        {
            Log.Logger.LogError(ex, "Failed to load UI document asset {AssetId} ({RelativePath})", Id, RelativePath);
            return null;
        }
    }

    /// <summary>Drops the cached document for <paramref name="assetId"/>, forcing a reload on next access.</summary>
    /// <param name="assetId">The document asset id.</param>
    public static void InvalidateCacheEntry(Guid assetId) => cache.TryRemove(assetId, out _);

    /// <summary>Clears every cached document. Call on project unload.</summary>
    public static void ClearCache() => cache.Clear();

    /// <summary>
    /// Loads and parses a <c>.ui</c> or baked <c>.amui</c> file directly from disk, bypassing the
    /// asset database. Used by tooling and tests.
    /// </summary>
    /// <param name="absolutePath">Absolute path of the <c>.ui</c> / <c>.amui</c> file.</param>
    public static UiDocument LoadContent(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return ParseText(File.ReadAllText(absolutePath), absolutePath);
    }

    /// <summary>Parses document text as baked JSON when it looks like JSON, otherwise as <c>.ui</c> XML.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="sourcePath">Path recorded for diagnostics, or <c>null</c>.</param>
    public static UiDocument ParseText(string text, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{')
            ? UiDocument.FromJson(text)
            : UiXmlParser.Parse(text, sourcePath);
    }
}
