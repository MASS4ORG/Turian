namespace Turian.Editor.Core;

/// <summary>Builds project tests and owns their collectible assemblies for CLI and Studio runs.</summary>
public sealed class ProjectTestRunner(ILogger logger)
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Builds a fresh test assembly, then discovers or runs its tests without starting a player.</summary>
    /// <param name="settings">The project to test.</param>
    /// <param name="filter">A case-insensitive test-name substring.</param>
    /// <param name="listOnly">Whether to discover without executing tests.</param>
    /// <param name="cancellationToken">Cancels the build or stops between test cases.</param>
    public async Task<SceneTestReport> RunAsync(IAppSettings settings, string? filter = null,
        bool listOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrickAssemblies.Load(ProjectPackages.ResolveOrEmpty(settings.ProjectAbsoluteDir), logger,
                settings.ProjectAbsoluteDir);
            var build = (BuildAppSettings)new BuildAppSettings().Load(settings);
            ProjectSettingsLoader.LoadFromSources(build);
            var project = CsProjectGenerator.GenerateTests(build, logger);
            GeneratedProject.Save(project);
            var output = Path.Combine(build.CacheAbsoluteDir, "Tests", "bin");
            await CompileAsync(project.FullPath, output, cancellationToken).ConfigureAwait(false);
            return Execute(Path.Combine(output, (build.TitleToPathFriendly ?? "Usercode") + ".Tests.dll"),
                filter, listOnly, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or MASS4.Bricks.PackageException
                                      or UnauthorizedAccessException or BadImageFormatException or ReflectionTypeLoadException)
        {
            return new SceneTestReport([new SceneTestResult("(test build)", SceneTestStatus.Failed,
                Message: error.Message, StackTrace: error.ToString())]);
        }
        finally { Gate.Release(); }
    }

    static async Task CompileAsync(string project, string output, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(DotnetLocator.Find())
        {
            WorkingDirectory = Path.GetDirectoryName(project),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "build", project, "-o", output, "-c", "Debug", "-m:4", "-nodeReuse:false" })
        {
            start.ArgumentList.Add(argument);
        }
        CompilerBase.WithoutLocatorEnvironment(start);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var diagnostic = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) { throw new InvalidOperationException(diagnostic); }
    }

    static SceneTestReport Execute(string path, string? filter, bool listOnly, CancellationToken cancellationToken)
    {
        var context = new SceneTestLoadContext(path);
        try
        {
            using var dll = File.OpenRead(path);
            using var pdb = File.OpenRead(Path.ChangeExtension(path, ".pdb"));
            var assembly = context.LoadFromStream(dll, pdb);
            return SceneTestRunner.Run([assembly], filter, listOnly, cancellationToken, Path.GetDirectoryName(path));
        }
        finally
        {
            CollectibleAssemblies.Release([.. context.Assemblies]);
            ObjectState.ReleaseCollectible();
            context.Unload();
        }
    }
}

/// <summary>Shares host contracts while keeping user test and component assemblies collectible.</summary>
sealed class SceneTestLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    readonly AssemblyDependencyResolver resolver = new(assemblyPath);

    protected override ReflectionAssembly? Load(AssemblyName assemblyName)
    {
        var shared = Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == assemblyName.Name);
        if (shared is not null) { return shared; }
        var path = resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null) { return null; }
        using var dll = File.OpenRead(path);
        var symbols = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(symbols)) { return LoadFromStream(dll); }
        using var pdb = File.OpenRead(symbols);
        return LoadFromStream(dll, pdb);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path
            ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
}
