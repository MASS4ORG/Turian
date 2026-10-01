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
        ProjectValidator.Report(ProjectValidator.Validate(directory), Log.Logger);

        var s = ReadSettings(directory);
        ProjectSettingsLoader.LoadFromSources(s);
        Log.Logger.LogInformation("{Title}", s.Title);
        return s;
    }

    /// <summary>
    /// Reimports assets and/or compiles and loads the user-code assembly before a headless command
    /// reads the project. Both steps go through one <see cref="BuildManager"/>: it is a process-wide
    /// singleton whose constructor throws if one already exists and whose <see cref="BuildManager.Dispose"/>
    /// never clears that singleton, so a command that needs both steps would throw constructing a
    /// second instance for the other.
    /// </summary>
    /// <param name="settings">The project's build settings.</param>
    /// <param name="reimport">Whether to reimport assets (the existing <c>--reimport</c> behavior).</param>
    /// <param name="loadUserCode">
    /// Whether to compile and load the user-code assembly (<c>--load-usercode</c>). Without this,
    /// user-defined components deserialize as <c>MissingComponent</c> and never run — the same
    /// limitation the Studio's own hot-reload exists to avoid, just never wired into this CLI
    /// until now. A compile failure is logged and does not abort the command: the caller still
    /// gets whatever it asked for, just with <c>MissingComponent</c> placeholders, same as before
    /// this flag existed.
    /// </param>
    static async Task PrepareProjectAsync(BuildAppSettings settings, bool reimport, bool loadUserCode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!reimport && !loadUserCode) return;

        var appSettings = new AppSettings().Load(settings) as AppSettings
            ?? throw new InvalidOperationException("Failed to create AppSettings for CLI asset import.");

        using var buildManager = new BuildManager(appSettings, Log.Logger);
        buildManager.UpdateSettings(settings);

        if (reimport)
        {
            var assetsDir = settings.AssetsAbsoluteDir;
            Directory.CreateDirectory(settings.CacheAbsoluteDir);

            Log.Logger.LogInformation("Importing assets from {AssetsDir}…", assetsDir);

            var assetDatabase = new AssetDatabase();
            var settingsService = new SettingsService();
            settingsService.Set(appSettings);

            using var importer = new AssetImporter(Log.Logger, assetDatabase, settingsService);
            importer.GenerateMetaFiles(assetsDir);

            Log.Logger.LogInformation("Asset import completed");
        }

        if (loadUserCode)
        {
            Log.Logger.LogInformation("Compiling and loading user code…");
            var status = await buildManager.CompileAndLoadAssemblyAsync().ConfigureAwait(false);

            if (status.State == BuildTaskState.Succeeded)
                Log.Logger.LogInformation("User code loaded: {Message}", status.Message);
            else
                Log.Logger.LogWarning(
                    "User code did not load ({State}): {Message}. Custom components will report as MissingComponent",
                    status.State, status.Message);
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
