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
        var csprojFilePath = await SetupAsync(GenerateCsProjFile).ConfigureAwait(false);

        var assemblyOutputPath = SlotAssemblyOutputPath(outputDirectory);
        EnsureDirectory(assemblyOutputPath);

        var cache = new UserCodeCompileCache(Logger);

        if (cache.IsAssemblyUpToDate(Settings, assemblyOutputPath, csprojFilePath, forceRecompile: forceRecompile))
        {
            Logger.LogInformation("Compilation skipped. Cached assembly is still valid: {Path}", assemblyOutputPath);
            if (!UserCodeTypeManifest.Exists(assemblyOutputPath))
                GenerateAndSaveTypeManifest(assemblyOutputPath);
            return assemblyOutputPath;
        }

        using var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(csprojFilePath).ConfigureAwait(false);
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Compilation returned null.");

        int errorCount, warningCount;
        await using (var fs = new FileStream(assemblyOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var result = compilation.Emit(fs);
            (errorCount, warningCount) = CompilationDiagnosticsReporter.Report(result.Diagnostics, Logger);

            if (!result.Success)
            {
                cache.Invalidate(Settings.ProjectAbsoluteDir, assemblyOutputPath);
                Logger.LogError(
                    "Compilation failed: {ErrorCount} error(s), {WarningCount} warning(s)",
                    errorCount,
                    warningCount);
                throw new InvalidOperationException("Compilation failed.");
            }
        }

        workspace.CloseSolution();

        GenerateAndSaveTypeManifest(assemblyOutputPath);

        var manifest = cache.CreateManifest(Settings, assemblyOutputPath, csprojFilePath);
        cache.SaveManifest(Settings.ProjectAbsoluteDir, assemblyOutputPath, manifest);

        Logger.LogInformation(
            "Compilation succeeded: {Path} ({WarningCount} warning(s))",
            assemblyOutputPath,
            warningCount);
        return assemblyOutputPath;
    }

    string GenerateCsProjFile()
    {
        var project = CsProjectGenerator.GenerateUserCode(Settings, Logger);
        project.Save();
        return project.FullPath;
    }

    void GenerateAndSaveTypeManifest(string assemblyOutputPath)
    {
        var typeManifest = UserCodeTypeManifestGenerator.Generate(
            Settings.AssetsAbsoluteDir, Logger, Path.GetFileNameWithoutExtension(assemblyOutputPath));
        UserCodeTypeManifest.Save(typeManifest, assemblyOutputPath);
    }
}
