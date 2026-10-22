namespace Turian.Editor.CLI;

public static partial class Program
{
    static Option<string?> SceneOption() =>
        new("--scene") { Description = "Scene asset id or path; defaults to the project's StartupScene" };

    static Option<bool> ReimportOption() =>
        new("--reimport") { Description = "Reimport the project's assets before loading" };

    static Option<string?> LocaleOption() =>
        new("--locale") { Description = "BCP-47 locale to force for the session, e.g. pt-BR; defaults to the project's" };

    // ── Helpers ────────────────────────────────────────────────────────────────

    static BuildAppSettings CreateSettings(FileSystemInfo info)
    {
        var directory = SettingsService.ResolveProjectDirectory(info.FullName)
                        ?? throw new DirectoryNotFoundException($"{info.FullName} is not a project folder.");
        ProjectSettingsFiles.MigrateLegacyProject(directory);
        ProjectSettingsFiles.MigrateToProjectAsset(directory);
        if (!ProjectValidator.Report(ProjectValidator.Validate(directory), Log.Logger))
        {
            throw new InvalidOperationException($"{directory} cannot be opened by this version of Turian.");
        }

        if (ProjectSettingsFiles.RecordEngineVersion(directory) is { } previous)
        {
            Log.Logger.LogInformation("Upgraded the project from Turian {Previous} to {Current}",
                previous.Length == 0 ? "(unrecorded)" : previous, ProjectSettingsFiles.EngineVersionStamp);
        }

        var s = ReadSettings(directory);
        ProjectSettingsLoader.LoadFromSources(s);
        Log.Logger.LogInformation("{Title}", s.Title);
        return s;
    }

    /// <summary>
    /// Prepares a headless project's assets and user code. The caller keeps the returned manager alive until
    /// scene processing finishes, then disposes it to unload user code.
    /// </summary>
    /// <param name="settings">The project's build settings.</param>
    /// <param name="reimport">Whether to reimport assets (the existing <c>--reimport</c> behavior).</param>
    /// <param name="loadUserCode">
    /// Whether to compile and load user code so its components can deserialize. Compilation failures are logged
    /// and leave unavailable component types as placeholders.
    /// </param>
    /// <returns>The preparation lifetime, or null when no preparation was requested.</returns>
    internal static async Task<BuildManager?> PrepareProjectAsync(BuildAppSettings settings, bool reimport, bool loadUserCode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!reimport && !loadUserCode)
        {
            return null;
        }

        var appSettings = new AppSettings().Load(settings) as AppSettings
            ?? throw new InvalidOperationException("Failed to create AppSettings for CLI asset import.");

        var buildManager = new BuildManager(appSettings, Log.Logger);
        try
        {
            buildManager.UpdateSettings(settings);

            if (reimport)
            {
                var assetsDir = settings.AssetsAbsoluteDir;
                Directory.CreateDirectory(settings.CacheAbsoluteDir);

                Log.Logger.LogInformation("Importing assets from {AssetsDir}…", assetsDir);

                var assetDatabase = new AssetDatabase();
                var settingsService = new SettingsService();
                settingsService.Set(appSettings);

                using var importer = new AssetImporter(Log.Logger, assetDatabase, settingsService, buildManager);
                importer.GenerateMetaFiles(assetsDir);

                Log.Logger.LogInformation("Asset import completed");
            }

            if (loadUserCode)
            {
                Log.Logger.LogInformation("Compiling and loading user code…");
                // User types deriving from or holding a precast brick's types resolve against its loaded assemblies.
                BrickAssemblies.Load(ProjectPackages.ResolveOrEmpty(settings.ProjectAbsoluteDir), Log.Logger, settings.ProjectAbsoluteDir);
                var status = await buildManager.CompileAndLoadAssemblyAsync().ConfigureAwait(false);

                if (status.State == BuildTaskState.Succeeded)
                {
                    Log.Logger.LogInformation("User code loaded: {Message}", status.Message);
                }
                else
                {
                    Log.Logger.LogWarning(
                        "User code did not load ({State}): {Message}. Custom components will report as MissingComponent",
                        status.State, status.Message);
                }
            }

            return buildManager;
        }
        catch
        {
            buildManager.Dispose();
            throw;
        }
    }

    static Option<bool> LoadUserCodeOption() =>
        new("--load-usercode")
        {
            Description = "Compile and load the user-code assembly first, so custom components run "
                          + "instead of deserializing as MissingComponent"
        };

    static BuildAppSettings ReadSettings(string projectDirectory)
    {
        var appSettings = SettingsService.Load(projectDirectory)
                          ?? throw new DirectoryNotFoundException($"{projectDirectory} is not a project folder.");

        return new BuildAppSettings().Load(appSettings) as BuildAppSettings
               ?? throw new InvalidOperationException("Project settings could not be copied.");
    }
}
