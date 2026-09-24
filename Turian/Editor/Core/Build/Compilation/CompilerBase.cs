namespace Turian.Editor.Core;

/// <summary>
/// Common setup and helpers shared by all compiler/exporter implementations.
/// </summary>
public abstract class CompilerBase(IAppSettings settings, ILogger logger)
{
    const string restoreStampFileName = "restore.stamp";

    /// <summary>App settings resolved at construction time.</summary>
    protected readonly BuildAppSettings Settings = new BuildAppSettings().Load(settings) as BuildAppSettings
        ?? throw new InvalidOperationException("Settings could not be loaded.");

    /// <summary>Logger instance.</summary>
    protected readonly ILogger Logger = logger;

    // ── Setup ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates settings, ensures MSBuildLocator is registered, generates the .csproj,
    /// and restores NuGet packages only when the generated project inputs changed.
    /// </summary>
    protected async Task<string> SetupAsync(Func<string> generateCsProjFile)
    {
        ArgumentNullException.ThrowIfNull(generateCsProjFile);

        ValidateStartupSceneSettings();
        ProjectSettingsLoader.WriteIndex(Settings.AssetsAbsoluteDir,
            Path.Combine(Settings.CacheAbsoluteDir, ProjectSettingsLoader.IndexFileName));
        ProjectIcon.Write(Settings, Settings.CacheAbsoluteDir);

        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        var csprojFilePath = generateCsProjFile();
        if (string.IsNullOrEmpty(csprojFilePath))
        {
            Logger.LogError("csproj generation returned an empty path");
            throw new InvalidOperationException("Compilation failed.");
        }

        try
        {
            if (ShouldRestorePackages(csprojFilePath))
            {
                await RestorePackages(csprojFilePath).ConfigureAwait(false);
                UpdateRestoreStamp(csprojFilePath);
            }
            else
            {
                Logger.LogInformation("Skipping package restore; project inputs unchanged: {Path}", csprojFilePath);
            }
        }
        catch (Exception e)
        {
            Logger.LogError("Package restore failed: {Error}", e.Message);
        }

        return csprojFilePath;
    }

    // ── Output path helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a fixed DLL output path inside <paramref name="slotDirectory"/> so the
    /// A/B swap strategy writes to a deterministic location.
    /// </summary>
    protected string SlotAssemblyOutputPath(string slotDirectory) =>
        Path.Combine(slotDirectory, $"{Settings.TitleToPathFriendly}.dll");

    /// <summary>Returns the "publish" output directory for an export build.</summary>
    protected static string ExportOutputDir(string absolutePath, string pathInter) =>
        Path.Combine(absolutePath, pathInter);

    // ── Utilities ──────────────────────────────────────────────────────────────

    /// <summary>Ensures the directory of <paramref name="path"/> exists.</summary>
    protected static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Restores NuGet packages for the given project.</summary>
    public async Task RestorePackages(string csprojFilePath)
    {
        var folder = Path.GetDirectoryName(csprojFilePath);
        Logger.LogInformation("Restoring packages: {Path}", folder);

        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{folder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            Logger.LogError("Package restore failed.\nSTDOUT:\n{Out}\nSTDERR:\n{Err}", stdout, stderr);
            throw new InvalidOperationException("Package restore failed.");
        }

        Logger.LogInformation("Package restore succeeded");
    }

    // ── Restore caching ────────────────────────────────────────────────────────

    bool ShouldRestorePackages(string csprojFilePath)
    {
        var restoreStampPath = GetRestoreStampPath(csprojFilePath);
        if (!File.Exists(restoreStampPath))
        {
            return true;
        }

        var stampLastWriteUtc = File.GetLastWriteTimeUtc(restoreStampPath);
        return IsRestoreInputNewerThanStamp(csprojFilePath, stampLastWriteUtc);
    }

    void UpdateRestoreStamp(string csprojFilePath)
    {
        var restoreStampPath = GetRestoreStampPath(csprojFilePath);
        EnsureDirectory(restoreStampPath);
        File.WriteAllText(
            restoreStampPath,
            $"RestoredAtUtc={DateTimeOffset.UtcNow:O}{Environment.NewLine}Project={csprojFilePath}");
    }

    bool IsRestoreInputNewerThanStamp(string csprojFilePath, DateTime stampLastWriteUtc)
    {
        if (File.GetLastWriteTimeUtc(csprojFilePath) > stampLastWriteUtc)
        {
            return true;
        }

        var directory = Path.GetDirectoryName(csprojFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return true;
        }

        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(filePath, GetRestoreStampPath(csprojFilePath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(filePath) > stampLastWriteUtc)
            {
                return true;
            }
        }

        return false;
    }

    static string GetRestoreStampPath(string csprojFilePath)
    {
        var directory = Path.GetDirectoryName(csprojFilePath)
            ?? throw new InvalidOperationException("Could not determine generated project directory.");

        return Path.Combine(directory, restoreStampFileName);
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    void ValidateStartupSceneSettings()
    {
        if (Settings.Get<PlayerSettings>().StartupScene is not { IsEmpty: false })
        {
            const string message = "Build failed: the project's Player Settings must define a valid StartupScene.";
            Logger.LogError(message);
            throw new InvalidOperationException(message);
        }
    }
}
