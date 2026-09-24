namespace Turian.Engine.Core;

/// <summary>A settings asset found in a project's sources.</summary>
/// <param name="Kind">The concrete <see cref="ProjectSettingsAsset"/> type the file holds.</param>
/// <param name="SourcePath">Absolute path of the <c>.dataasset</c>.</param>
/// <param name="AssetId">The id its meta file gives it.</param>
public sealed record ProjectSettingsSource(Type Kind, string SourcePath, Guid AssetId);

/// <summary>
/// Finds a project's settings assets and reads them into <see cref="IAppSettings.Loaded"/>. Nothing lists
/// them: in a project's sources every data asset whose class is a <see cref="ProjectSettingsAsset"/> is
/// one, and a built game reads the ids its build wrote to <see cref="IndexFileName"/>, because packed
/// assets have no meta files to look through.
/// </summary>
public static class ProjectSettingsLoader
{
    /// <summary>
    /// The file a build writes beside the game, listing the settings asset ids — it is also how a built
    /// game recognizes the folder it runs from.
    /// </summary>
    public const string IndexFileName = "projectSettings.json";

    /// <summary>The folder under <c>Assets</c> whose copy of a kind wins when a project holds more than one.</summary>
    public const string PreferredFolderName = "Settings";

    static readonly string[] dataAssetExtensions = [".dataasset", ".asset", ".data"];

    /// <summary>
    /// Every settings asset under a project's <c>Assets</c> folder, one entry per file, with the one each
    /// kind uses first: the copy under <c>Assets/Settings</c>, then the first by path.
    /// </summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>The settings assets found.</returns>
    public static IReadOnlyList<ProjectSettingsSource> FindSources(string assetsDirectory)
    {
        if (string.IsNullOrWhiteSpace(assetsDirectory) || !Directory.Exists(assetsDirectory)) return [];

        var preferred = Path.Combine(assetsDirectory, PreferredFolderName) + Path.DirectorySeparatorChar;
        var found = new List<ProjectSettingsSource>();

        foreach (var sourcePath in DataAssetSources(assetsDirectory))
        {
            if (PeekKind(sourcePath) is not { } kind) continue;

            try
            {
                if (Asset.Load($"{sourcePath}.meta") is { } meta) found.Add(new(kind, sourcePath, meta.Id));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Logger.LogWarning(ex, "Project settings {Path} has an unreadable meta file", sourcePath);
            }
        }

        return
        [
            .. found
                .OrderBy(source => source.SourcePath.StartsWith(preferred, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(static source => source.SourcePath, StringComparer.Ordinal),
        ];
    }

    /// <summary>Reads the settings each kind uses from the project's sources. No import is needed.</summary>
    /// <param name="settings">The project whose settings are loaded.</param>
    public static void LoadFromSources(IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Loaded.Clear();

        foreach (var source in FindSources(settings.AssetsAbsoluteDir).DistinctBy(static source => source.Kind))
        {
            try
            {
                if (DataAsset.LoadContent(source.SourcePath) is ProjectSettingsAsset loaded)
                    settings.Loaded.Use(loaded);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Logger.LogWarning(ex, "Project settings {Path} could not be read", source.SourcePath);
            }
        }
    }

    /// <summary>
    /// Reads the settings a built game lists in <see cref="IndexFileName"/> through its asset loader. A
    /// folder without the index but with an <c>Assets</c> folder is read from its sources instead.
    /// </summary>
    /// <param name="settings">The project whose settings are loaded.</param>
    /// <param name="loader">Resolves the asset metadata by id.</param>
    public static void Load(IAppSettings settings, IAssetLoader? loader)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var indexPath = Path.Combine(settings.ProjectAbsoluteDir, IndexFileName);
        if (!File.Exists(indexPath))
        {
            LoadFromSources(settings);
            return;
        }

        settings.Loaded.Clear();
        if (loader is null) return;

        foreach (var assetId in ReadIndex(indexPath))
        {
            try
            {
                if (new AssetReference<DataAssetAsset>(assetId).LoadAsync(loader).GetAwaiter().GetResult()
                        ?.GetContent(settings.ProjectAbsoluteDir) is ProjectSettingsAsset loaded)
                    settings.Loaded.Use(loaded);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Logger.LogWarning(ex, "Project settings {AssetId} could not be loaded", assetId);
            }
        }
    }

    /// <summary>Writes the index a built game reads its settings through: the asset each kind uses.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <param name="indexPath">Where to write <see cref="IndexFileName"/>.</param>
    public static void WriteIndex(string assetsDirectory, string indexPath)
    {
        var ids = FindSources(assetsDirectory).DistinctBy(static source => source.Kind)
            .Select(static source => source.AssetId);

        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        File.WriteAllText(indexPath, JsonSerializer.Serialize(new { Settings = ids }));
    }

    /// <summary>The data-asset source files under a folder that have a meta file beside them.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>Absolute source paths.</returns>
    public static IEnumerable<string> DataAssetSources(string assetsDirectory) =>
        Directory.EnumerateFiles(assetsDirectory, "*.meta", SearchOption.AllDirectories)
            .Select(static meta => meta[..^".meta".Length])
            .Where(static source => dataAssetExtensions.Contains(Path.GetExtension(source),
                StringComparer.OrdinalIgnoreCase))
            .Where(File.Exists);

    /// <summary>
    /// The settings kind a data asset holds, read from its <c>__TypeId</c> without deserializing the rest,
    /// so looking through a project's data assets costs a few bytes each. Null for anything else.
    /// </summary>
    static Type? PeekKind(string sourcePath)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(sourcePath));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals(ObjectJsonSerializer<DataAsset>.TypeIdProperty))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                return reader.Read() && reader.TryGetGuid(out var id)
                       && TypeRegistry.TryGetType(id, out var type)
                       && type is not null && typeof(ProjectSettingsAsset).IsAssignableFrom(type)
                    ? type
                    : null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Logger.LogDebug(ex, "{Path} is not a readable data asset", sourcePath);
        }

        return null;
    }

    static IEnumerable<Guid> ReadIndex(string indexPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));

        return document.RootElement.TryGetProperty("Settings", out var ids)
            ? [.. ids.EnumerateArray().Select(static id => id.GetGuid())]
            : [];
    }
}
