namespace Turian.Editor.Core;

/// <summary>
/// Publishes the user project as a standalone executable and packages assets.
/// </summary>
public sealed class ExportUserCode(
    IAppSettings settings,
    ILogger logger,
    BuildConfiguration configuration = BuildConfiguration.Release)
    : CompilerBase(settings, logger), IUserCodeCompiler
{
    static readonly string[] artifactExtensionsToDelete =
        [".pdb", ".xml", ".deps.json"];

    /// <summary>Publishes the project and returns the export output directory.</summary>
    public async Task<string> ExecuteAsync()
    {
        var csprojFilePath = await SetupAsync(GenerateCsProjFile).ConfigureAwait(false);
        EnsureDirectory(csprojFilePath);

        var exportOutputPath = ExportOutputDir(Settings.ProjectAbsoluteDir, "Output");
        Logger.LogInformation("Export output path: {Path}", exportOutputPath);
        EnsureDirectory(exportOutputPath + Path.DirectorySeparatorChar);

        var configName = configuration == BuildConfiguration.Release ? "Release" : "Debug";

        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"publish \"{csprojFilePath}\" -o \"{exportOutputPath}\" -c {configName}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Logger.LogError("Export failed: {Output}", output);
            throw new InvalidOperationException("Export failed.");
        }

        CleanArtifacts(exportOutputPath);
        RenameExecutable(Settings, csprojFilePath, exportOutputPath);
        CopyProjectSettings(exportOutputPath);
        WriteTypeManifest(exportOutputPath);
        PackageAssets(exportOutputPath);

        Logger.LogInformation("Export succeeded");
        return exportOutputPath;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    string GenerateCsProjFile()
    {
        var project = CsProjectGenerator.GenerateExecutable(Settings, Logger);
        project.Save();
        return project.FullPath;
    }

    /// <summary>
    /// Removes PDB, XML documentation, and other dotnet build artifacts from the
    /// export output directory that are not needed at runtime.
    /// </summary>
    void CleanArtifacts(string outputDirectory)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (artifactExtensionsToDelete.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not delete artifact: {File}", file);
                }
            }
        }

        Logger.LogInformation("Removed {Count} build artifact(s) from export output", removed);
    }

    static void RenameExecutable(BuildAppSettings settings, string csprojFilePath, string exportOutputPath)
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var exportedExePath = Path.Combine(
            exportOutputPath,
            $"{Path.GetFileNameWithoutExtension(csprojFilePath)}{extension}");

        var newExePath = Path.Combine(exportOutputPath, $"{settings.TitleToPathFriendly}{extension}");

        if (File.Exists(exportedExePath))
            File.Move(exportedExePath, newExePath, overwrite: true);
    }

    /// <summary>Copies the settings index — how the game finds its settings and its folder — and the icon.</summary>
    void CopyProjectSettings(string exportOutputPath)
    {
        foreach (var fileName in new[] { ProjectSettingsLoader.IndexFileName, ProjectIcon.PngFileName })
        {
            var sourcePath = Path.Combine(Settings.CacheAbsoluteDir, fileName);
            if (!File.Exists(sourcePath))
            {
                if (fileName == ProjectSettingsLoader.IndexFileName)
                    Logger.LogWarning("Project settings index not found at {Path}. Skipping its copy", sourcePath);
                continue;
            }

            var destinationPath = Path.Combine(exportOutputPath, fileName);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            Logger.LogInformation("Copied {File} to export output: {Path}", fileName, destinationPath);
        }
    }

    /// <summary>
    /// Writes the manifest the game registers user components through; without it every component
    /// declared in the project's scripts loads as a <see cref="MissingComponent"/>.
    /// </summary>
    void WriteTypeManifest(string exportOutputPath)
    {
        var manifest = UserCodeTypeManifestGenerator.Generate(
            Settings.AssetsAbsoluteDir, Logger, Settings.TitleToPathFriendly);
        UserCodeTypeManifest.Save(manifest, Path.Combine(exportOutputPath, $"{Settings.TitleToPathFriendly}.dll"));
    }

    void PackageAssets(string exportOutputPath)
    {
        var cacheCatalogPath = Path.Combine(Settings.ProjectAbsoluteDir, ".Cache", "assetCatalog.json");
        if (!File.Exists(cacheCatalogPath))
        {
            Logger.LogWarning("Asset catalog not found at {Path}. Skipping asset packaging", cacheCatalogPath);
            return;
        }

        var packBuilder = new OapArchiveBuilder(Logger);
        var packResult = packBuilder.Build(
            Settings.ProjectAbsoluteDir,
            exportOutputPath,
            new OapPackOptions { Name = Settings.TitleToPathFriendly, Version = "1.0.0" });

        Logger.LogInformation(
            "Packaged {Count} assets → {PackPath}",
            packResult.EntryCount,
            packResult.OapFilePath);
    }
}
