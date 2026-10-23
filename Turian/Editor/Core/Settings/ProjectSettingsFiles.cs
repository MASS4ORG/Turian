namespace Turian.Editor.Core;

/// <summary>
/// Writes a project's settings assets and its <see cref="ProjectAsset"/>, which registers them. New projects get
/// one per built-in kind under <c>Assets/Settings</c>; an older project is converted when it is opened: a
/// <c>project.data</c> into settings assets, and a project without a project asset into one.
/// </summary>
public static class ProjectSettingsFiles
{
    const string extension = ".dataasset";

    /// <summary>The engine version a project records: without build metadata such as the commit.</summary>
    public static string EngineVersionStamp =>
        new MASS4.Bricks.SemanticVersion(ProjectPackages.EngineVersion.Major, ProjectPackages.EngineVersion.Minor,
            ProjectPackages.EngineVersion.Patch, ProjectPackages.EngineVersion.Prerelease).ToString();

    /// <summary>
    /// Writes the project asset, registering <paramref name="settings"/> and <paramref name="buildProfiles"/> in
    /// order, stamped with this engine's version.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="settings">Asset ids of the settings in force, at most one per kind.</param>
    /// <param name="buildProfiles">Asset ids of the build profiles.</param>
    /// <param name="activeBuildProfile">The id of the active profile, or <c>null</c> for none.</param>
    /// <returns>Absolute path of the written asset.</returns>
    public static string CreateProjectAsset(IAppSettings project, IEnumerable<Guid> settings,
        IEnumerable<Guid> buildProfiles, Guid? activeBuildProfile = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var asset = new ProjectAsset
        {
            EngineVersion = EngineVersionStamp,
            Settings = [.. settings.Select(static id => new DataAssetReference<ProjectSettingsAsset>(id))],
            BuildProfiles =
                [.. buildProfiles.Select(static id => new DataAssetReference<ProjectSettingsCollection>(id))],
            ActiveBuildProfile = activeBuildProfile is { } active
                ? new DataAssetReference<ProjectSettingsCollection>(active)
                : null,
        };
        return SaveProjectAsset(project, asset);
    }

    /// <summary>
    /// Registers a newly created asset in the project asset: a build profile always, a settings asset when its kind
    /// has none registered yet. A second settings asset of a kind stays unregistered, for a build profile to use
    /// as an override. Projects without a project asset are left unchanged.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="asset">The created payload.</param>
    /// <param name="assetId">The created asset's id.</param>
    /// <returns>True when the project asset changed.</returns>
    public static bool RegisterCreated(IAppSettings project, DataAsset asset, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        if (ProjectAsset.Load(project.AssetsAbsoluteDir) is not { } registry)
        {
            return false;
        }

        switch (asset)
        {
            case BuildProfile:
                registry.BuildProfiles.Add(new DataAssetReference<ProjectSettingsCollection>(assetId));
                break;
            case ProjectSettingsAsset settings when ProjectSettingsLoader.FindSources(project.AssetsAbsoluteDir)
                .All(s => s.Kind != settings.GetType()):
                registry.Settings.Add(new DataAssetReference<ProjectSettingsAsset>(assetId));
                break;
            default:
                return false;
        }

        SaveProjectAsset(project, registry);
        return true;
    }

    /// <summary>
    /// Writes a build profile into <c>Assets/Settings/Build</c>, named after the profile, and registers it in the
    /// project asset.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="profile">The profile; its <see cref="BuildProfile.Name"/> names the file.</param>
    /// <param name="active">Whether builds without a named profile follow it from now on.</param>
    /// <returns>Absolute path of the written asset.</returns>
    public static string CreateBuildProfile(IAppSettings project, BuildProfile profile, bool active = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);

        var directory = Path.Combine(project.AssetsAbsoluteDir, ProjectSettingsLoader.PreferredFolderName, "Build");
        Directory.CreateDirectory(directory);
        var name = string.IsNullOrWhiteSpace(profile.Name) ? "BuildProfile" : profile.Name.Trim();
        var path = UniquePath(directory, name);
        var meta = new DataAssetAsset
        {
            Id = Guid.NewGuid(),
            RelativePath = Path.GetRelativePath(project.ProjectAbsoluteDir, path),
        };
        profile.Id = meta.Id;
        Serializer.Save<DataAsset>(path, profile);
        Serializer.Save($"{path}.meta", meta);
        RegisterCreated(project, profile, meta.Id);

        if (active && ProjectAsset.Load(project.AssetsAbsoluteDir) is { } registry)
        {
            registry.ActiveBuildProfile = new DataAssetReference<ProjectSettingsCollection>(meta.Id);
            SaveProjectAsset(project, registry);
        }

        return path;
    }

    /// <summary>
    /// Adds a build profile for <paramref name="target"/>, named after it and unique among the project's profiles,
    /// as the Build Profiles window's Add does. The new profile is not made active.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="target">What the profile builds.</param>
    /// <returns>Absolute path of the written asset.</returns>
    public static string AddBuildProfile(IAppSettings project, BuildTarget target)
    {
        ArgumentNullException.ThrowIfNull(project);

        var taken = BuildProfiles.List(project).Select(static p => p.Profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = target.ToString();
        for (var index = 1; taken.Contains(name); index++)
        {
            name = $"{target} {index}";
        }

        return CreateBuildProfile(project, new BuildProfile { Name = name, Target = target });
    }

    /// <summary>
    /// Makes a registered build profile the active one, as Unity's Switch Profile: Build &amp; Run, exports and play
    /// mode follow it from now on. <c>null</c> builds the plain project.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="profileId">The profile's asset id, or <c>null</c> for none.</param>
    /// <returns>False when the project has no project asset or does not register the profile.</returns>
    public static bool SetActiveBuildProfile(IAppSettings project, Guid? profileId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (ProjectAsset.Load(project.AssetsAbsoluteDir) is not { } registry)
        {
            return false;
        }

        if (profileId is { } id && registry.BuildProfiles.All(r => r.AssetId != id))
        {
            return false;
        }

        registry.ActiveBuildProfile = profileId is { } active
            ? new DataAssetReference<ProjectSettingsCollection>(active)
            : null;
        SaveProjectAsset(project, registry);
        return true;
    }

    /// <summary>
    /// Gives a project that has no project asset one, registering what a search of its <c>Assets</c> folder finds:
    /// the settings asset each kind used so far, and every build profile. Duplicates a kind had are reported and
    /// stay unregistered.
    /// </summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <returns>True when the project asset was created.</returns>
    public static bool MigrateToProjectAsset(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var project = SettingsService.Load(projectDirectory);
        if (project is null || File.Exists(ProjectAsset.PathIn(project.AssetsAbsoluteDir)))
        {
            return false;
        }

        var found = ProjectSettingsLoader.Search(project.AssetsAbsoluteDir);
        LogDuplicates(projectDirectory, found);
        var profiles = BuildProfiles.Search(project.AssetsAbsoluteDir)
            .Select(static p => Asset.Load($"{p.Path}.meta")?.Id).OfType<Guid>();
        var settings = found.DistinctBy(static s => s.Kind).Select(static s => s.AssetId);
        var path = CreateProjectAsset(project, settings, profiles);
        Log.Logger.LogInformation("Registered the project's settings and build profiles in {Path}",
            Path.GetRelativePath(projectDirectory, path));
        return true;
    }

    static void LogDuplicates(string projectDirectory, IReadOnlyList<ProjectSettingsSource> found)
    {
        foreach (var kind in found.GroupBy(static s => s.Kind).Where(static g => g.Count() > 1))
        {
            Log.Logger.LogWarning("{Kind}: registered {Used}; {Ignored} left unregistered", kind.Key.Name,
                Path.GetRelativePath(projectDirectory, kind.First().SourcePath),
                string.Join(", ", kind.Skip(1).Select(s => Path.GetRelativePath(projectDirectory, s.SourcePath))));
        }
    }

    /// <summary>
    /// Stamps the project asset with this engine's version when it records an older one or none: what opening a
    /// project with a newer engine does once it has been upgraded.
    /// </summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <returns>The version the project recorded before, or <c>null</c> when nothing changed.</returns>
    public static string? RecordEngineVersion(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var project = SettingsService.Load(projectDirectory);
        if (project is null || ProjectAsset.Load(project.AssetsAbsoluteDir) is not { } registry)
        {
            return null;
        }

        var current = ProjectPackages.EngineVersion;
        if (MASS4.Bricks.SemanticVersion.TryParse(registry.EngineVersion, out var recorded) && recorded >= current)
        {
            return null;
        }

        var previous = registry.EngineVersion ?? string.Empty;
        registry.EngineVersion = EngineVersionStamp;
        SaveProjectAsset(project, registry);
        return previous;
    }

    // A data asset's payload id is its asset id, so the meta file decides it.
    static string SaveProjectAsset(IAppSettings project, ProjectAsset asset)
    {
        var path = ProjectAsset.PathIn(project.AssetsAbsoluteDir);
        Directory.CreateDirectory(project.AssetsAbsoluteDir);
        if (File.Exists($"{path}.meta") && Asset.Load($"{path}.meta") is { } meta)
        {
            asset.Id = meta.Id;
        }
        else
        {
            asset.Id = Guid.NewGuid();
            Serializer.Save($"{path}.meta", new DataAssetAsset
            {
                Id = asset.Id,
                RelativePath = Path.GetRelativePath(project.ProjectAbsoluteDir, path),
            });
        }

        Serializer.Save<DataAsset>(path, asset);
        return path;
    }

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
        settings.Id = meta.Id;

        Serializer.Save<DataAsset>(path, settings);
        Serializer.Save($"{path}.meta", meta);
        RegisterCreated(project, settings, meta.Id);

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

        if (Locate(project, type) is { } existing)
        {
            return existing;
        }

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
        if (!File.Exists(legacyPath))
        {
            return false;
        }

        var project = SettingsService.Load(projectDirectory)
            ?? throw new InvalidOperationException($"{projectDirectory} has no Assets folder.");
        ProjectSettingsLoader.LoadFromSources(project);

        using (var document = JsonDocument.Parse(File.ReadAllText(legacyPath)))
        {
            var root = document.RootElement;

            if (Locate(project, typeof(PlayerSettings)) is null)
            {
                Create(project, new PlayerSettings
                {
                    ProductName = String(root, "ProductName") ?? String(root, "Title"),
                    Author = String(root, "CompanyName") ?? String(root, "Author"),
                    ApplicationIdentifier = String(root, "ApplicationIdentifier"),
                    Version = String(root, "Version"),
                    StartupScene = Reference<Prefab>(root, "StartupScene"),
                });
            }

            if (Locate(project, typeof(InputSettings)) is null)
            {
                Create(project, new InputSettings { Actions = Reference<DataAssetAsset>(root, "InputActions") });
            }

            if (Locate(project, typeof(GraphicsSettings)) is null)
            {
                Create(project, new GraphicsSettings
                {
                    TextureMaxResolution = root.TryGetProperty("TextureMaxResolution", out var max)
                                           && max.ValueKind == JsonValueKind.Number
                        ? max.GetInt32()
                        : 0,
                });
            }
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
        {
            path = Path.Combine(directory, $"{name} {index}{extension}");
        }

        return path;
    }
}
