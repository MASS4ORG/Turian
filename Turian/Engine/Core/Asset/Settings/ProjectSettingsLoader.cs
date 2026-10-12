namespace Turian.Engine.Core;

/// <summary>A settings asset found in a project's sources.</summary>
/// <param name="Kind">The concrete <see cref="ProjectSettingsAsset"/> type the file holds.</param>
/// <param name="SourcePath">Absolute path of the <c>.dataasset</c>.</param>
/// <param name="AssetId">The id its meta file gives it.</param>
public sealed record ProjectSettingsSource(Type Kind, string SourcePath, Guid AssetId);

/// <summary>
/// Finds a project's settings assets and reads them into <see cref="IAppSettings.Loaded"/>. In a project's sources
/// the <see cref="ProjectAsset"/> registers them; a project not yet given one is searched for every data asset whose
/// class is a <see cref="ProjectSettingsAsset"/>. A built game reads the ids its build wrote to
/// <see cref="IndexFileName"/>, because packed assets have no meta files to look through.
/// </summary>
public static class ProjectSettingsLoader
{
    /// <summary>
    /// The file a build writes beside the game, listing the settings asset ids — it is also how a built
    /// game recognizes the folder it runs from.
    /// </summary>
    public const string IndexFileName = "projectSettings.json";

    /// <summary>
    /// The folder under <c>Assets</c> settings assets are created in, and whose copy of a kind wins a search.
    /// </summary>
    public const string PreferredFolderName = "Settings";

    static readonly string[] DataAssetExtensions = [".dataasset", ".asset", ".data"];

    /// <summary>
    /// The settings assets of a project, one entry per file, with the one each kind uses first: the ones its
    /// <see cref="ProjectAsset"/> registers, in order; without a project asset, every settings asset under
    /// <c>Assets</c>, as <see cref="Search"/> orders them.
    /// </summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>The settings assets found.</returns>
    public static IReadOnlyList<ProjectSettingsSource> FindSources(string assetsDirectory)
    {
        if (string.IsNullOrWhiteSpace(assetsDirectory) || !Directory.Exists(assetsDirectory)) return [];

        return ProjectAsset.Load(assetsDirectory) is { } project
            ? Registered(project, new AssetSourceIndex(ProjectDirectory(assetsDirectory)))
            : Search(assetsDirectory);
    }

    /// <summary>
    /// The settings asset each kind uses: the project's, replaced kind by kind by <paramref name="overrides"/>,
    /// such as a build profile's.
    /// </summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <param name="overrides">Settings replacing the project's of the same kind, or <c>null</c>.</param>
    /// <returns>One source per kind.</returns>
    public static IReadOnlyList<ProjectSettingsSource> EffectiveSources(string assetsDirectory,
        ProjectSettingsCollection? overrides = null)
    {
        var sources = FindSources(assetsDirectory).DistinctBy(static source => source.Kind).ToList();
        if (overrides is null || !Directory.Exists(assetsDirectory)) return sources;

        foreach (var replacement in Registered(overrides, new AssetSourceIndex(ProjectDirectory(assetsDirectory)))
                     .DistinctBy(static source => source.Kind))
        {
            sources.RemoveAll(source => source.Kind == replacement.Kind);
            sources.Add(replacement);
        }

        return sources;
    }

    /// <summary>Reads the settings each kind uses from the project's sources. No import is needed.</summary>
    /// <param name="settings">The project whose settings are loaded.</param>
    /// <param name="overrides">Settings replacing the project's of the same kind, such as a build profile's.</param>
    public static void LoadFromSources(IAppSettings settings, ProjectSettingsCollection? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Loaded.Clear();
        var index = new AssetSourceIndex(settings.ProjectAbsoluteDir);
        foreach (var source in EffectiveSources(settings.AssetsAbsoluteDir, overrides))
        {
            try
            {
                // Through the index, so a variant reads its bases by id.
                if (index.LoadFile(source.SourcePath) is ProjectSettingsAsset loaded) settings.Loaded.Use(loaded);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                           or InvalidOperationException)
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
        if (string.IsNullOrWhiteSpace(settings.ProjectAbsoluteDir))
        {
            LoadFromSources(settings);
            return;
        }

        Load(settings, loader, new FileContentDirectory(settings.ProjectAbsoluteDir));
    }

    /// <summary>
    /// Reads the settings a built game lists in <see cref="IndexFileName"/> at the root of its content. Content
    /// without the index is read from the project's sources instead.
    /// </summary>
    /// <param name="settings">The project whose settings are loaded.</param>
    /// <param name="loader">Resolves the asset metadata by id.</param>
    /// <param name="content">The game's content root.</param>
    public static void Load(IAppSettings settings, IAssetLoader? loader, IContentDirectory content)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(content);

        if (!content.Exists(IndexFileName))
        {
            LoadFromSources(settings);
            return;
        }

        settings.Loaded.Clear();
        if (loader is null) return;

        foreach (var assetId in ReadIndex(content.ReadAllText(IndexFileName)))
        {
            try
            {
                if (loader.LoadContentAsync<ProjectSettingsAsset>(assetId).GetAwaiter().GetResult()
                    is { } loaded)
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
    /// <param name="overrides">Settings replacing the project's of the same kind, such as a build profile's.</param>
    public static void WriteIndex(string assetsDirectory, string indexPath, ProjectSettingsCollection? overrides = null)
    {
        var ids = EffectiveSources(assetsDirectory, overrides).Select(static source => source.AssetId);

        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        File.WriteAllText(indexPath, JsonSerializer.Serialize(new { Settings = ids }));
    }

    /// <summary>The data-asset source files under a folder that have a meta file beside them.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>Absolute source paths.</returns>
    public static IEnumerable<string> DataAssetSources(string assetsDirectory) =>
        Directory.EnumerateFiles(assetsDirectory, "*.meta", SearchOption.AllDirectories)
            .Select(static meta => meta[..^".meta".Length])
            .Where(static source => DataAssetExtensions.Contains(Path.GetExtension(source),
                StringComparer.OrdinalIgnoreCase))
            .Where(File.Exists);

    /// <summary>
    /// Every settings asset under <c>Assets</c>, the copy under <c>Assets/Settings</c> first, then by path: how a
    /// project without a <see cref="ProjectAsset"/> finds its settings, and what migrating it registers.
    /// </summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>The settings assets found.</returns>
    public static IReadOnlyList<ProjectSettingsSource> Search(string assetsDirectory)
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

    static List<ProjectSettingsSource> Registered(ProjectSettingsCollection collection, AssetSourceIndex index)
    {
        var found = new List<ProjectSettingsSource>();
        foreach (var reference in collection.Settings.Where(static r => !r.IsEmpty))
        {
            if (index.PathOf(reference.AssetId) is { } path && PeekKind(path) is { } kind)
                found.Add(new(kind, path, reference.AssetId));
        }

        return found;
    }

    static string ProjectDirectory(string assetsDirectory) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetsDirectory)))!;

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

    static IEnumerable<Guid> ReadIndex(string indexJson)
    {
        using var document = JsonDocument.Parse(indexJson);

        return document.RootElement.TryGetProperty("Settings", out var ids)
            ? [.. ids.EnumerateArray().Select(static id => id.GetGuid())]
            : [];
    }
}
