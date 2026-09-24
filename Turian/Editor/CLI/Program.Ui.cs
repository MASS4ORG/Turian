namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command UiCommand()
    {
        var outputOption = new Option<string>("--out") { Description = "PNG file to write", DefaultValueFactory = _ => "ui.png" };
        var widthOption = new Option<int>("--width") { Description = "Output width in pixels", DefaultValueFactory = _ => 1280 };
        var heightOption = new Option<int>("--height") { Description = "Output height in pixels", DefaultValueFactory = _ => 720 };
        var fileOption = new Option<string?>("--file")
        {
            Description = "Render a .ui document",
        };
        var dataOption = new Option<string?>("--data")
        {
            Description = "JSON object (inline or @path) bound as the document's data context",
        };

        var cmd = new Command("ui", "Render a .ui document to a PNG (no scene, no GPU)")
        {
            outputOption, widthOption, heightOption, fileOption, dataOption
        };

        cmd.SetAction(result =>
        {
            var width = result.GetValue(widthOption);
            var height = result.GetValue(heightOption);
            var outputPath = Path.GetFullPath(result.GetValue(outputOption)!);
            var file = result.GetValue(fileOption);

            Action<Guinevere.Gui> build;
            if (!string.IsNullOrEmpty(file))
            {
                var path = Path.GetFullPath(file);
                var document = UiXmlParser.Parse(File.ReadAllText(path), path);
                var baseDir = Path.GetDirectoryName(path)!;

                // Probe the .ui folder, then walk up for an Assets/ root so project-relative
                // image paths (Assets/Textures/UI/…png) resolve without a full project.
                var assetsRoot = baseDir;
                for (var d = new DirectoryInfo(baseDir); d is not null; d = d.Parent)
                    if (string.Equals(d.Name, "Assets", StringComparison.OrdinalIgnoreCase))
                    {
                        assetsRoot = d.Parent?.FullName ?? baseDir;
                        break;
                    }

                var renderer = new UiRenderer(document)
                {
                    ImageResolver = new UiImageResolver(null, baseDir, assetsRoot).Resolve,
                    FontResolver = new UiFontResolver(null, baseDir, assetsRoot).Resolve,
                };
                foreach (var src in document.StyleSheets)
                {
                    var ussPath = Path.GetFullPath(Path.Combine(baseDir, src));
                    if (File.Exists(ussPath))
                        renderer.StyleSheets.Add(Guinevere.StyleSheet.Parse(File.ReadAllText(ussPath)));
                    else
                        Log.Logger.LogWarning("Stylesheet not found: {Path}", ussPath);
                }

                var data = result.GetValue(dataOption);
                if (!string.IsNullOrEmpty(data))
                {
                    var json = data.StartsWith('@') ? File.ReadAllText(data[1..]) : data;
                    renderer.Bind(JsonToDictionary(json));
                }

                build = renderer.Render;
                Log.Logger.LogInformation("Parsed {Path}: {Elements} elements, {Styles} stylesheet(s)",
                    path, document.Elements().Count(), renderer.StyleSheets.Count);
            }
            else
            {
                throw new InvalidOperationException("The --file option is required.");
            }

            var png = UiImageRenderer.RenderPng(build, width, height, background: new SKColor(12, 14, 20));
            File.WriteAllBytes(outputPath, png);

            Log.Logger.LogInformation("Wrote {Width}×{Height} UI render: {OutputPath}", width, height, outputPath);
        });

        return cmd;
    }

    /// <summary>Parses a JSON object into a nested <see cref="Dictionary{TKey,TValue}"/> for binding.</summary>
    static Dictionary<string, object?> JsonToDictionary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (Dictionary<string, object?>)ConvertElement(doc.RootElement)!;

        static object? ConvertElement(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ConvertElement(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().Select(ConvertElement).ToList(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

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
