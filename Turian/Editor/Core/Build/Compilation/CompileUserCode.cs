namespace Turian.Editor.Core;

/// <summary>
/// Compiles the user project as a class-library DLL.
/// The output is written to the caller-supplied <paramref name="outputDirectory"/> so
/// that <see cref="Turian.Editor.Core.AssemblySlotManager"/> can manage the A/B slot rotation.
/// </summary>
public sealed class CompileUserCode(
    IAppSettings settings,
    ILogger logger,
    string outputDirectory,
    bool forceRecompile = false)
    : CompilerBase(settings, logger), IUserCodeCompiler
{
    /// <summary>
    /// Compiles the user project and returns the path of the produced DLL.
    /// If the current source/input fingerprint matches a previously successful compilation,
    /// recompilation is skipped and the cached DLL is reused.
    /// Pass <c>forceRecompile: true</c> at construction to bypass the cache unconditionally.
    /// </summary>
    public async Task<string> ExecuteAsync()
    {
        var graph = CsProjectGenerator.DiscoverAssemblies(Settings);
        var csprojFilePaths = await SetupAsync(() => GenerateCsProjFiles(graph)).ConfigureAwait(false);
        var csprojFilePath = csprojFilePaths[^1];

        var assemblyOutputPath = SlotAssemblyOutputPath(outputDirectory);
        EnsureDirectory(assemblyOutputPath);

        var cache = new UserCodeCompileCache(Logger);

        if (cache.IsAssemblyUpToDate(Settings, assemblyOutputPath, csprojFilePath, forceRecompile: forceRecompile)
            && graph.Definitions.All(a => File.Exists(Path.Combine(outputDirectory, $"{a.Name}.dll"))))
        {
            Logger.LogInformation("Compilation skipped. Cached assembly is still valid: {Path}", assemblyOutputPath);
            if (!UserCodeTypeManifest.Exists(assemblyOutputPath))
                GenerateAndSaveTypeManifest(assemblyOutputPath, graph);
            return assemblyOutputPath;
        }

        // The slot is loaded as a whole, so an assembly whose definition was removed must not linger in it.
        foreach (var stale in Directory.EnumerateFiles(outputDirectory, "*.dll"))
            File.Delete(stale);

        using var workspace = MSBuildWorkspace.Create();
        foreach (var path in csprojFilePaths.Reverse())
        {
            if (workspace.CurrentSolution.Projects.Any(p => PathsEqual(p.FilePath, path))) continue;
            await workspace.OpenProjectAsync(path).ConfigureAwait(false);
        }

        int errorCount = 0, warningCount = 0;
        var solution = workspace.CurrentSolution;
        foreach (var projectId in solution.GetProjectDependencyGraph().GetTopologicallySortedProjects())
        {
            var project = solution.GetProject(projectId)!;
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Compilation of {project.AssemblyName} returned null.");

            await using var fs = new FileStream(Path.Combine(outputDirectory, $"{project.AssemblyName}.dll"),
                FileMode.Create, FileAccess.Write, FileShare.None);
            var result = compilation.Emit(fs);
            var (errors, warnings) = CompilationDiagnosticsReporter.Report(result.Diagnostics, Logger);
            errorCount += errors;
            warningCount += warnings;

            if (!result.Success)
            {
                cache.Invalidate(Settings.ProjectAbsoluteDir, assemblyOutputPath);
                Logger.LogError(
                    "Compilation of {Assembly} failed: {ErrorCount} error(s), {WarningCount} warning(s)",
                    project.AssemblyName,
                    errors,
                    warnings);
                throw new InvalidOperationException("Compilation failed.");
            }
        }

        workspace.CloseSolution();

        GenerateAndSaveTypeManifest(assemblyOutputPath, graph);

        var manifest = cache.CreateManifest(Settings, assemblyOutputPath, csprojFilePath);
        cache.SaveManifest(Settings.ProjectAbsoluteDir, assemblyOutputPath, manifest);

        Logger.LogInformation(
            "Compilation succeeded: {Path} ({WarningCount} warning(s))",
            assemblyOutputPath,
            warningCount);
        return assemblyOutputPath;
    }

    IReadOnlyList<string> GenerateCsProjFiles(AssemblyGraph graph)
    {
        var projects = CsProjectGenerator.GenerateUserCodeProjects(Settings, Logger, graph);
        foreach (var project in projects) project.Save();
        return [.. projects.Select(static project => project.FullPath)];
    }

    static bool PathsEqual(string? left, string right) =>
        left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    void GenerateAndSaveTypeManifest(string assemblyOutputPath, AssemblyGraph graph)
    {
        var typeManifest = UserCodeTypeManifestGenerator.Generate(
            Settings.AssetsAbsoluteDir, Logger, Path.GetFileNameWithoutExtension(assemblyOutputPath), graph);
        UserCodeTypeManifest.Save(typeManifest, assemblyOutputPath);
    }
}
