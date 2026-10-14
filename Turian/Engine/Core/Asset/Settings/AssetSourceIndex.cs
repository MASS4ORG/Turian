namespace Turian.Engine.Core;

/// <summary>
/// Finds the source file of an asset id in a project without importing it: through the editor's asset catalog
/// first, then through the <c>.meta</c> files for ids the catalog does not know yet. Also reads the bases of
/// data-asset variants by id.
/// </summary>
/// <param name="projectDirectory">The project folder.</param>
public sealed class AssetSourceIndex(string projectDirectory)
{
    Dictionary<Guid, string>? catalog;
    Dictionary<Guid, string>? metas;

    /// <summary>The source file of <paramref name="assetId"/>.</summary>
    /// <param name="assetId">The asset id.</param>
    /// <returns>The absolute path, or <c>null</c> when no file has that id.</returns>
    public string? PathOf(Guid assetId)
    {
        catalog ??= ReadCatalog();
        if (catalog.TryGetValue(assetId, out var path) && File.Exists(path))
        {
            return path;
        }

        metas ??= ReadMetas();
        return metas.GetValueOrDefault(assetId);
    }

    /// <summary>Reads the data asset with <paramref name="assetId"/>, flattening it when it is a variant.</summary>
    /// <param name="assetId">The asset id.</param>
    /// <returns>The payload, or <c>null</c> when the id has no file.</returns>
    public DataAsset? Load(Guid assetId) =>
        PathOf(assetId) is { } path ? DataAsset.LoadContent(path, assetId, ReadText) : null;

    /// <summary>Reads the data asset at <paramref name="path"/>, flattening it when it is a variant.</summary>
    /// <param name="path">The data asset's file.</param>
    /// <returns>The payload.</returns>
    public DataAsset? LoadFile(string path) => DataAsset.LoadContent(path, null, ReadText);

    string? ReadText(Guid assetId) => PathOf(assetId) is { } path ? File.ReadAllText(path) : null;

    Dictionary<Guid, string> ReadCatalog()
    {
        var database = new AssetDatabase();
        return database.LoadCatalogFromProject(projectDirectory) == AssetCatalogLoadStatus.Loaded
            ? database.Assets.Values.Where(static r => r.SourceRelativePath.Length > 0)
                .ToDictionary(static r => r.AssetId,
                    static r => Path.GetFullPath(Path.Combine(r.ProjectRootPath, r.SourceRelativePath)))
            : [];
    }

    Dictionary<Guid, string> ReadMetas()
    {
        var assets = Path.Combine(projectDirectory, "Assets");
        var found = new Dictionary<Guid, string>();
        if (!Directory.Exists(assets))
        {
            return found;
        }

        foreach (var meta in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
        {
            if (PeekId(meta) is { } id)
            {
                found.TryAdd(id, meta[..^".meta".Length]);
            }
        }

        return found;
    }

    static Guid? PeekId(string metaPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(metaPath));
            return document.RootElement.TryGetProperty("Id", out var id) && id.TryGetGuid(out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
