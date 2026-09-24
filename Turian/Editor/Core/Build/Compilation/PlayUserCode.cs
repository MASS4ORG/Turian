namespace Turian.Editor.Core;

/// <summary>
/// Result of launching the playable runtime process.
/// </summary>
/// <param name="Process">The started runtime process.</param>
/// <param name="RuntimePath">The launched executable or DLL path.</param>
public sealed record PlayLaunchResult(Process Process, string RuntimePath);

/// <summary>
/// Builds a debug runtime host for the current project and launches it as a child process.
/// Play mode always uses <see cref="BuildConfiguration.Debug"/>.
/// </summary>
public sealed class PlayUserCode(IAppSettings settings, ILogger logger, string? userCodeDllPath = null)
    : CompilerBase(settings, logger), IUserCodeCompiler
{
    const string playSourceSubDir = "Play";

    /// <summary>Builds the playable runtime and starts it; returns the runtime path.</summary>
    public async Task<string> ExecuteAsync()
    {
        var result = await ExecuteWithProcessAsync().ConfigureAwait(false);
        return result.RuntimePath;
    }

    /// <summary>Builds the playable runtime and starts it, returning the process handle.</summary>
    public async Task<PlayLaunchResult> ExecuteWithProcessAsync()
    {
        var projectDirectory = Path.Combine(Settings.CacheAbsoluteDir, playSourceSubDir);
        var outputDir = Path.Combine(projectDirectory, "bin", "Debug", Settings.TargetFramework);
        var dllPath = Path.Combine(outputDir, "project.dll");
        var exePath = Path.Combine(outputDir, "project.exe");

        if (userCodeDllPath is not null && IsPlayBuildUpToDate(dllPath, userCodeDllPath))
        {
            Logger.LogInformation("Play build skipped: inputs unchanged");
            CopyTypeManifestToOutput(outputDir);
            SyncAssetsToOutput(outputDir);
            return Launch(exePath, dllPath, outputDir);
        }

        var csprojFilePath = await SetupAsync(GeneratePlayCsProjFile).ConfigureAwait(false);
        EnsureDirectory(csprojFilePath);

        var buildProjectDirectory = Path.GetDirectoryName(csprojFilePath)
            ?? throw new InvalidOperationException("Could not determine play project directory.");

        Logger.LogInformation("Building play runtime: {Project}", csprojFilePath);

        var buildStartInfo = new ProcessStartInfo("dotnet", $"build \"{csprojFilePath}\" -c Debug")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = buildProjectDirectory
        };

        using (var buildProcess = new Process())
        {
            buildProcess.StartInfo = buildStartInfo;
            buildProcess.Start();

            var stdoutTask = buildProcess.StandardOutput.ReadToEndAsync();
            var stderrTask = buildProcess.StandardError.ReadToEndAsync();

            await buildProcess.WaitForExitAsync().ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (buildProcess.ExitCode != 0)
            {
                Logger.LogError("Play build failed.\nSTDOUT:\n{Out}\nSTDERR:\n{Err}", stdout, stderr);
                throw new InvalidOperationException("Play build failed.");
            }

            Logger.LogInformation("Play build succeeded");
        }

        if (userCodeDllPath is not null)
            SavePlayBuildStamp(projectDirectory, userCodeDllPath);

        CopyTypeManifestToOutput(outputDir);
        return Launch(exePath, dllPath, outputDir);
    }

    PlayLaunchResult Launch(string exePath, string dllPath, string outputDir)
    {
        string fileName, arguments, runtimePath;

        if (OperatingSystem.IsWindows() && File.Exists(exePath))
        {
            fileName = exePath;
            arguments = string.Empty;
            runtimePath = exePath;
        }
        else if (File.Exists(dllPath))
        {
            fileName = "dotnet";
            arguments = $"\"{dllPath}\"";
            runtimePath = dllPath;
        }
        else
        {
            throw new InvalidOperationException("Play runtime output not found after build.");
        }

        Logger.LogInformation("Launching play runtime: {File} {Args}", fileName, arguments);

        var runProcess = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = outputDir
        }) ?? throw new InvalidOperationException("Failed to launch play process.");

        return new PlayLaunchResult(runProcess, runtimePath);
    }

    void SyncAssetsToOutput(string outputDir)
    {
        var srcAssets = Path.Combine(Settings.CacheAbsoluteDir, "Assets");
        if (!Directory.Exists(srcAssets)) return;

        var dstAssets = Path.Combine(outputDir, ".Cache", "Assets");
        try
        {
            CopyDirectory(srcAssets, dstAssets);

            var srcCatalog = Path.Combine(Settings.CacheAbsoluteDir, "assetCatalog.json");
            if (File.Exists(srcCatalog))
            {
                var dstCatalogDir = Path.Combine(outputDir, ".Cache");
                Directory.CreateDirectory(dstCatalogDir);
                File.Copy(srcCatalog, Path.Combine(dstCatalogDir, "assetCatalog.json"), overwrite: true);
            }

            Logger.LogDebug("Synced assets to play output: {Path}", dstAssets);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to sync assets to play output directory");
        }
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }

    void CopyTypeManifestToOutput(string outputDir)
    {
        var dst = Path.Combine(outputDir, UserCodeTypeManifest.FileName);
        var src = userCodeDllPath is null ? null : UserCodeTypeManifest.ManifestPathFor(userCodeDllPath);
        if (src is null || !File.Exists(src))
        {
            // No compiled slot to copy from (the CLI builds straight from source), so generate it.
            var manifest = UserCodeTypeManifestGenerator.Generate(
                Settings.AssetsAbsoluteDir, Logger, Settings.TitleToPathFriendly);
            UserCodeTypeManifest.Save(manifest, Path.Combine(outputDir, $"{Settings.TitleToPathFriendly}.dll"));
            return;
        }

        try
        {
            File.Copy(src, dst, overwrite: true);
            Logger.LogDebug("Copied user-code type manifest to play output: {Dst}", dst);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to copy user-code type manifest to play output directory");
        }
    }

    // ── Play build cache ───────────────────────────────────────────────────────

    IEnumerable<string> EngineAssemblyPaths()
    {
        var root = CsProjectGenerator.TurianDir();
        if (string.IsNullOrEmpty(root)) yield break;

        foreach (var (relativeDir, name) in Settings.TurianPackages)
        {
            var publishedPath = Path.Combine(root, "lib", $"{name}.dll");
            yield return File.Exists(publishedPath)
                ? publishedPath
                : Path.Combine(root, relativeDir, "bin", "Debug", Settings.TargetFramework, $"{name}.dll");
        }
    }

    static string GetStampPath(string projectDirectory) =>
        Path.Combine(projectDirectory, "play.build.stamp.json");

    bool IsPlayBuildUpToDate(string outputDllPath, string ucDllPath)
    {
        if (!File.Exists(outputDllPath))
        {
            Logger.LogDebug("Play build cache miss: output DLL not found at {Path}", outputDllPath);
            return false;
        }

        // The play project links the engine assemblies by HintPath, so a rebuilt engine does not
        // change the user-code fingerprint. Rebuild when any referenced engine DLL is newer than
        // the play output — otherwise an engine fix never reaches a launched game.
        var outputWriteTime = File.GetLastWriteTimeUtc(outputDllPath);
        foreach (var enginePath in EngineAssemblyPaths())
        {
            if (File.Exists(enginePath) && File.GetLastWriteTimeUtc(enginePath) > outputWriteTime)
            {
                Logger.LogDebug("Play build cache miss: engine assembly {Path} is newer than the play output", enginePath);
                return false;
            }
        }

        var projectDirectory = Path.Combine(Settings.CacheAbsoluteDir, playSourceSubDir);
        var stampPath = GetStampPath(projectDirectory);
        if (!File.Exists(stampPath))
        {
            Logger.LogDebug("Play build cache miss: stamp not found");
            return false;
        }

        PlayBuildStamp? stamp;
        try { stamp = Serializer.Load<PlayBuildStamp>(stampPath); }
        catch { return false; }

        if (stamp is null) return false;

        var ucManifest = LoadUserCodeManifest(ucDllPath);
        if (ucManifest is null)
        {
            Logger.LogDebug("Play build cache miss: user code manifest not found");
            return false;
        }

        if (!string.Equals(stamp.SourceFingerprint, ucManifest.SourceFingerprint, StringComparison.Ordinal))
        {
            Logger.LogDebug("Play build cache miss: source fingerprint changed");
            return false;
        }

        if (!string.Equals(stamp.SettingsFingerprint, ucManifest.SettingsFingerprint, StringComparison.Ordinal))
        {
            Logger.LogDebug("Play build cache miss: settings fingerprint changed");
            return false;
        }

        Logger.LogDebug("Play build cache hit: play output is still valid at {Path}", outputDllPath);
        return true;
    }

    void SavePlayBuildStamp(string projectDirectory, string ucDllPath)
    {
        var ucManifest = LoadUserCodeManifest(ucDllPath);
        if (ucManifest is null) return;

        var stamp = new PlayBuildStamp
        {
            SourceFingerprint = ucManifest.SourceFingerprint,
            SettingsFingerprint = ucManifest.SettingsFingerprint,
        };

        try
        {
            Serializer.Save(GetStampPath(projectDirectory), stamp);
            Logger.LogDebug("Play build stamp saved");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save play build stamp");
        }
    }

    UserCodeCompileCacheManifest? LoadUserCodeManifest(string ucDllPath)
    {
        var manifestPath = UserCodeCompileCache.GetManifestPath(Settings.ProjectAbsoluteDir, ucDllPath);
        if (!File.Exists(manifestPath)) return null;
        try { return Serializer.Load<UserCodeCompileCacheManifest>(manifestPath); }
        catch { return null; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    string GeneratePlayCsProjFile()
    {
        var project = CsProjectGenerator.GeneratePlayableExecutable(Settings, Logger);
        var playDir = Path.Combine(Settings.CacheAbsoluteDir, playSourceSubDir);
        Directory.CreateDirectory(playDir);
        project.Save();
        return project.FullPath;
    }
}

/// <summary>
/// Stamp persisted after a successful play build to detect when the build can be skipped.
/// </summary>
public sealed class PlayBuildStamp
{
    /// <summary>Gets or sets the source fingerprint from the last successful user code compilation.</summary>
    public string? SourceFingerprint { get; set; }
    /// <summary>Gets or sets the settings fingerprint from the last successful user code compilation.</summary>
    public string? SettingsFingerprint { get; set; }
}
