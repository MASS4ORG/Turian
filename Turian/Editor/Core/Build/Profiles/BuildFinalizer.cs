namespace Turian.Editor.Core;

/// <summary>
/// The post-publish steps shared by the build platforms: trimming build artifacts, naming the executable,
/// placing settings, writing the type manifest and packaging content.
/// </summary>
static class BuildFinalizer
{
    static readonly string[] ArtifactExtensionsToDelete =
        [".pdb", ".xml", ".deps.json"];

    /// <summary>Removes PDB, XML documentation and other build artifacts the runtime does not need.</summary>
    /// <param name="context">The published project and output.</param>
    internal static void CleanArtifacts(BuildPlatformContext context)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(context.OutputDirectory, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!ArtifactExtensionsToDelete.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "Could not delete artifact: {File}", file);
            }
        }

        context.Logger.LogInformation("Removed {Count} build artifact(s) from export output", removed);
    }

    /// <summary>Renames the published executable to the project's title with the platform's extension.</summary>
    /// <param name="context">The published project and output.</param>
    /// <param name="extension">The executable's extension for the target runtime.</param>
    internal static void RenameExecutable(BuildPlatformContext context, string extension)
    {
        var exportedExePath = Path.Combine(context.OutputDirectory,
            $"{Path.GetFileNameWithoutExtension(context.ProjectFile)}{extension}");
        var newExePath = Path.Combine(context.OutputDirectory, $"{context.Settings.TitleToPathFriendly}{extension}");

        if (File.Exists(exportedExePath))
            File.Move(exportedExePath, newExePath, overwrite: true);
    }

    /// <summary>Copies the settings index — how the game finds its settings and folder — and the icon.</summary>
    /// <param name="context">The project and output.</param>
    internal static void CopyProjectSettings(BuildPlatformContext context)
    {
        foreach (var fileName in new[] { ProjectSettingsLoader.IndexFileName, ProjectIcon.PngFileName })
        {
            var sourcePath = Path.Combine(context.Settings.CacheAbsoluteDir, fileName);
            if (!File.Exists(sourcePath))
            {
                if (fileName == ProjectSettingsLoader.IndexFileName)
                    context.Logger.LogWarning("Project settings index not found at {Path}. Skipping its copy", sourcePath);
                continue;
            }

            var destinationPath = Path.Combine(context.OutputDirectory, fileName);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            context.Logger.LogInformation("Copied {File} to export output: {Path}", fileName, destinationPath);
        }
    }

    /// <summary>
    /// Writes the manifest the game registers user components through; without it every component declared
    /// in the project's scripts loads as a <c>MissingComponent</c>.
    /// </summary>
    /// <param name="context">The project and output.</param>
    internal static void WriteTypeManifest(BuildPlatformContext context)
    {
        var manifest = UserCodeTypeManifestGenerator.Generate(
            context.Settings.AssetsAbsoluteDir, context.Logger, context.Settings.TitleToPathFriendly,
            CsProjectGenerator.DiscoverAssemblies(context.Settings), context.Profile?.Target ?? BuildTarget.Client);
        UserCodeTypeManifest.Save(manifest,
            Path.Combine(context.OutputDirectory, $"{context.Settings.TitleToPathFriendly}.dll"));
    }

    /// <summary>Packs the project's imported assets into an OAP beside the game.</summary>
    /// <param name="context">The project and output.</param>
    internal static void PackageAssets(BuildPlatformContext context)
    {
        var cacheCatalogPath = Path.Combine(context.Settings.ProjectAbsoluteDir, ".Cache", "assetCatalog.json");
        if (!File.Exists(cacheCatalogPath))
        {
            context.Logger.LogWarning("Asset catalog not found at {Path}. Skipping asset packaging", cacheCatalogPath);
            return;
        }

        var packBuilder = new OapArchiveBuilder(context.Logger);
        var packResult = packBuilder.Build(
            context.Settings.ProjectAbsoluteDir,
            context.OutputDirectory,
            new OapPackOptions { Name = context.Settings.TitleToPathFriendly, Version = "1.0.0" });

        context.Logger.LogInformation(
            "Packaged {Count} assets → {PackPath}",
            packResult.EntryCount,
            packResult.OapFilePath);
    }
}
