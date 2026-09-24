namespace Turian.Editor.Core;

/// <summary>
/// Writes a project's settings assets. New projects get one per built-in kind under
/// <c>Assets/Settings</c>, and an older project that still has a <c>project.data</c> is converted to that
/// layout when it is opened.
/// </summary>
public static class ProjectSettingsFiles
{
    const string extension = ".dataasset";

    /// <summary>
    /// Writes a settings asset into <c>Assets/Settings</c> and puts it in force.
    /// </summary>
    /// <param name="project">The project to add to.</param>
    /// <param name="settings">The settings to write.</param>
    /// <returns>Absolute path of the written asset.</returns>
    public static string Create(IAppSettings project, ProjectSettingsAsset settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.Combine(project.AssetsAbsoluteDir, ProjectSettingsLoader.PreferredFolderName);
        Directory.CreateDirectory(directory);

        var type = settings.GetType();
        var name = type.GetCustomAttribute<CreateAssetMenuAttribute>()?.FileName ?? type.Name;
        var path = UniquePath(directory, name);
        var meta = new DataAssetAsset
        {
            Id = Guid.NewGuid(),
            RelativePath = Path.GetRelativePath(project.ProjectAbsoluteDir, path),
        };

        Serializer.Save<DataAsset>(path, settings);
        Serializer.Save($"{path}.meta", meta);

        project.Loaded.Use(settings);
        return path;
    }

    /// <summary>
    /// The source file of the settings asset a kind uses, creating one with the kind's defaults when the
    /// project has none. What opening a settings kind from a menu does.
    /// </summary>
    /// <param name="project">The open project.</param>
    /// <param name="type">A concrete <see cref="ProjectSettingsAsset"/> type.</param>
    /// <returns>Absolute path of the asset.</returns>
    public static string Ensure(IAppSettings project, Type type)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(type);

        if (Locate(project, type) is { } existing) return existing;

        return Activator.CreateInstance(type) is ProjectSettingsAsset created
            ? Create(project, created)
            : throw new ArgumentException($"{type.Name} is not a project settings asset.", nameof(type));
    }

    /// <summary>The source file of the settings asset a kind uses, or null when the project has none.</summary>
    /// <param name="project">The open project.</param>
    /// <param name="type">The settings kind.</param>
    /// <returns>Absolute path, or null.</returns>
    public static string? Locate(IAppSettings project, Type type)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(type);

        return ProjectSettingsLoader.FindSources(project.AssetsAbsoluteDir)
            .FirstOrDefault(source => source.Kind == type)?.SourcePath;
    }

    /// <summary>
    /// Converts a project that still has a <c>project.data</c>: values it carried inline move into the
    /// settings assets — its title becoming the product name when none was set — and the file is renamed
    /// to <c>project.data.bak</c>, which leaves the folder itself as the project.
    /// </summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <returns>True when the project was converted.</returns>
    public static bool MigrateLegacyProject(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var legacyPath = Path.Combine(projectDirectory, SettingsService.LegacyProjectFileName);
        if (!File.Exists(legacyPath)) return false;

        var project = SettingsService.Load(projectDirectory)
            ?? throw new InvalidOperationException($"{projectDirectory} has no Assets folder.");
        ProjectSettingsLoader.LoadFromSources(project);

        using (var document = JsonDocument.Parse(File.ReadAllText(legacyPath)))
        {
            var root = document.RootElement;

            if (Locate(project, typeof(PlayerSettings)) is null)
                Create(project, new PlayerSettings
                {
                    ProductName = String(root, "ProductName") ?? String(root, "Title"),
                    Author = String(root, "CompanyName") ?? String(root, "Author"),
                    ApplicationIdentifier = String(root, "ApplicationIdentifier"),
                    Version = String(root, "Version"),
                    StartupScene = Reference<Prefab>(root, "StartupScene"),
                });

            if (Locate(project, typeof(InputSettings)) is null)
                Create(project, new InputSettings { Actions = Reference<DataAssetAsset>(root, "InputActions") });

            if (Locate(project, typeof(GraphicsSettings)) is null)
                Create(project, new GraphicsSettings
                {
                    TextureMaxResolution = root.TryGetProperty("TextureMaxResolution", out var max)
                                           && max.ValueKind == JsonValueKind.Number
                        ? max.GetInt32()
                        : 0,
                });
        }

        File.Move(legacyPath, $"{legacyPath}.bak", overwrite: true);
        Log.Logger.LogInformation("Moved the settings in {Path} into Assets/{Folder}; the project is now its folder",
            legacyPath, ProjectSettingsLoader.PreferredFolderName);
        return true;
    }

    static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static AssetReference<TAsset>? Reference<TAsset>(JsonElement root, string name)
        where TAsset : Asset =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("AssetId", out var id) && id.TryGetGuid(out var assetId)
            ? new AssetReference<TAsset>(assetId)
            : null;

    static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name + extension);
        for (var index = 1; File.Exists(path); index++)
            path = Path.Combine(directory, $"{name} {index}{extension}");

        return path;
    }
}
