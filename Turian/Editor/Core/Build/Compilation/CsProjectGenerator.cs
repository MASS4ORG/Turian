namespace Turian.Editor.Core;

/// <summary>
/// Describes how an executable project should be generated.
/// </summary>
/// <summary>
/// Describes the target executable generation flow for runtime output.
/// </summary>
public enum ExecutableGenerationMode
{
    /// <summary>
    /// Generate a debug executable suitable for editor play mode.
    /// </summary>
    Play,

    /// <summary>
    /// Generate a release executable suitable for export/publish.
    /// </summary>
    Export
}

/// <summary>
/// Create CSProj
/// </summary>
public static class CsProjectGenerator
{
    const string codeGeneratorName = "Turian.CSharp.CodeGenerator";

    /// <summary>
    /// Generate a brand new .csproj file
    /// </summary>
    /// <param name="settings"></param>
    /// <param name="logger"></param>
    /// <returns></returns>
    /// <exception cref="System.InvalidOperationException"></exception>
    public static ProjectRootElement GenerateUserCode(IBuildAppSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return GenerateUserCode(settings, logger, DiscoverAssemblies(settings));
    }

    /// <summary>The default assembly's project: every script outside an assembly definition.</summary>
    /// <param name="settings">The project settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="graph">The project's assemblies.</param>
    /// <returns>The generated project, not yet saved.</returns>
    public static ProjectRootElement GenerateUserCode(IBuildAppSettings settings, ILogger logger, AssemblyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(graph);
        var csprojFilePath = UserCodeCsProjFullPath(settings);
        var isExecutable = false;

        var projectRoot = ProjectRootElement.Create(csprojFilePath);
        projectRoot.Sdk = settings.TargetSdk;
        projectRoot
            .AddProjectInfo(settings, executable: isExecutable)
            .AddTurianDir()
            .AddInternalDllReferences(settings)
            .AddNugetPackageReferences(settings)
            .AddAssemblyReferences(settings, graph.Default.References);
        projectRoot.AddCsFiles(settings, graph.ExcludedDirectories(graph.Default));

        return projectRoot;
    }

    /// <summary>
    /// Every project the user code compiles through: one per assembly definition, each after the ones it
    /// references, then the default assembly's.
    /// </summary>
    /// <param name="settings">The project settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="graph">The project's assemblies.</param>
    /// <returns>The generated projects, not yet saved.</returns>
    public static IReadOnlyList<ProjectRootElement> GenerateUserCodeProjects(
        IBuildAppSettings settings,
        ILogger logger,
        AssemblyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return [.. graph.Definitions.Select(assembly => GenerateAssemblyDefinition(settings, graph, assembly)),
            GenerateUserCode(settings, logger, graph)];
    }

    /// <summary>The project's assemblies, read from the assembly definitions under its <c>Assets</c> folder.</summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The project's assemblies.</returns>
    public static AssemblyGraph DiscoverAssemblies(IBuildAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return AssemblyGraph.Discover(settings.AssetsAbsoluteDir, settings.TitleToPathFriendly ?? string.Empty);
    }

    static ProjectRootElement GenerateAssemblyDefinition(
        IBuildAppSettings settings,
        AssemblyGraph graph,
        ProjectAssembly assembly)
    {
        var projectRoot = ProjectRootElement.Create(AssemblyCsProjFullPath(settings, assembly.Name));
        projectRoot.Sdk = settings.TargetSdk;
        projectRoot.AddProjectInfo(settings, executable: false).AddTurianDir();
        if (!assembly.NoEngineReferences) projectRoot.AddInternalDllReferences(settings);
        projectRoot
            .AddNugetPackageReferences(settings)
            .AddAssemblyReferences(settings, assembly.References);

        var group = projectRoot.AddPropertyGroup();
        group.AddProperty("AssemblyName", assembly.Name);
        group.AddProperty("RootNamespace", assembly.RootNamespace);
        group.AddProperty("EnableDefaultCompileItems", "false");
        if (assembly.AllowUnsafeCode) group.AddProperty("AllowUnsafeBlocks", "true");

        var projectDirectory = Path.GetDirectoryName(projectRoot.FullPath)!;
        var exclude = string.Join(';', ExcludePatterns(projectDirectory, graph.ExcludedDirectories(assembly)));
        var itemGroup = projectRoot.AddItemGroup();
        foreach (var directory in assembly.Directories)
            itemGroup.AddItem("Compile", $"{Path.GetRelativePath(projectDirectory, directory)}/**/*.cs").Exclude = exclude;

        return projectRoot;
    }

    static string UserCodeCsProjFullPath(IBuildAppSettings settings) => Path.Combine(
        settings.CacheAbsoluteDir,
        "Source",
        $"{settings.TitleToPathFriendly}.csproj");

    static string AssemblyCsProjFullPath(IBuildAppSettings settings, string assemblyName) => Path.Combine(
        settings.CacheAbsoluteDir,
        "Assemblies",
        assemblyName,
        $"{assemblyName}.csproj");

    static IEnumerable<string> ExcludePatterns(string projectDirectory, IEnumerable<string> excludedDirectories) =>
        excludedDirectories.Select(directory => $"{Path.GetRelativePath(projectDirectory, directory)}/**")
            .Prepend("**/obj/**");

    /// <summary>
    /// Generate a brand new .csproj file
    /// </summary>
    /// <param name="settings"></param>
    /// <param name="logger"></param>
    /// <param name="mode">Controls whether the executable is generated for play mode or export.</param>
    /// <returns></returns>
    /// <exception cref="System.InvalidOperationException"></exception>
    public static ProjectRootElement GenerateExecutable(
        IBuildAppSettings settings,
        ILogger logger,
        ExecutableGenerationMode mode = ExecutableGenerationMode.Export)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var csprojFilePath = Path.Combine(
            settings.CacheAbsoluteDir,
            mode == ExecutableGenerationMode.Play ? "Play" : settings.ExportSourceSubDir,
            "project.csproj");

        const bool isExecutable = true;

        var projectRoot = ProjectRootElement.Create(csprojFilePath);
        projectRoot.Sdk = settings.TargetSdk;
        projectRoot
            .AddProjectInfo(settings, isExecutable, mode)
            .AddTurianDir()
            .AddInternalDllReferences(settings)
            .AddUserProject(settings)
            .AddNugetPackageReferences(settings)
            .AddAssetsDir(settings)
            .CopyPlatformSpecificFiles();

        return projectRoot;
    }

    /// <summary>
    /// Generate a debug executable project used by play mode.
    /// </summary>
    public static ProjectRootElement GeneratePlayableExecutable(IBuildAppSettings settings, ILogger logger) =>
        GenerateExecutable(settings, logger, ExecutableGenerationMode.Play);

    /// <summary>
    /// The Turian checkout root: the nearest ancestor of this assembly that contains a
    /// <c>.slnx</c>. Empty when not running from a source tree.
    /// </summary>
    internal static string TurianDir()
    {
        if (IsPublishedDistribution())
        {
            return AppContext.BaseDirectory;
        }

        var directoryName = AppContext.BaseDirectory;

        while (directoryName != null)
        {
            if (Directory.GetFiles(directoryName, "*.slnx", SearchOption.TopDirectoryOnly).Length > 0)
            {
                return directoryName;
            }

            directoryName = Directory.GetParent(directoryName)?.FullName;
        }

        return string.Empty;
    }

    static bool IsPublishedDistribution() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory,
            $"turian-cli{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}"));

    extension(ProjectRootElement projectRoot)
    {
        private ProjectRootElement AddTurianDir()
        {
            var turianPropGroup = projectRoot.AddPropertyGroup();
            turianPropGroup.AddProperty("TurianDir", TurianDir());
            return projectRoot;
        }

        private ProjectRootElement AddAssetsDir(IBuildAppSettings settings)
        {
            var outputDirectory = Path.GetDirectoryName(projectRoot.FullPath)
                                  ?? throw new InvalidOperationException("Could not determine generated project directory.");

            var cacheAssetsAbsoluteDir = Path.Combine(settings.CacheAbsoluteDir, "Assets");
            var cacheAssetsRelativePath = Path.GetRelativePath(outputDirectory, cacheAssetsAbsoluteDir);
            var cacheCatalogAbsolutePath = Path.Combine(settings.CacheAbsoluteDir, "assetCatalog.json");
            var cacheCatalogRelativePath = Path.GetRelativePath(outputDirectory, cacheCatalogAbsolutePath);

            var cachedAssetsFiles = projectRoot.AddItemGroup();
            cachedAssetsFiles.AddItem("CachedAssetsFiles", Path.Combine(cacheAssetsRelativePath, "**", "*"));

            var cacheCatalogFiles = projectRoot.AddItemGroup();
            cacheCatalogFiles.AddItem("CacheCatalogFile", cacheCatalogRelativePath);

            // The settings index is what a built game reads its settings through, and what marks the folder
            // it runs from; CompilerBase writes it before the project is generated.
            var projectSettingsFiles = projectRoot.AddItemGroup();
            var projectSettingsRelativePath = Path.GetRelativePath(
                outputDirectory, Path.Combine(settings.CacheAbsoluteDir, ProjectSettingsLoader.IndexFileName));
            projectSettingsFiles.AddItem("ProjectSettingsFile", projectSettingsRelativePath);

            var iconPath = Path.Combine(settings.CacheAbsoluteDir, ProjectIcon.PngFileName);
            if (File.Exists(iconPath))
                projectSettingsFiles.AddItem("ProjectSettingsFile", Path.GetRelativePath(outputDirectory, iconPath));

            var target = projectRoot.AddTarget("CopyAssetContent");
            target.AfterTargets = "PostBuildEvent";

            var copyTask = target.AddTask("Copy");
            copyTask.SetParameter("SourceFiles", "@(CachedAssetsFiles)");
            copyTask.SetParameter("DestinationFiles", "@(CachedAssetsFiles->'$(OutDir)/.Cache/Assets/%(RecursiveDir)%(Filename)%(Extension)')");
            copyTask.Condition = "'$(Configuration)'=='Debug'";

            var copyCacheCatalogTask = target.AddTask("Copy");
            copyCacheCatalogTask.SetParameter("SourceFiles", "@(CacheCatalogFile)");
            copyCacheCatalogTask.SetParameter("DestinationFolder", "$(OutDir)/.Cache");
            copyCacheCatalogTask.Condition = "'$(Configuration)'=='Debug'";

            var copyProjectSettingsTask = target.AddTask("Copy");
            copyProjectSettingsTask.SetParameter("SourceFiles", "@(ProjectSettingsFile)");
            copyProjectSettingsTask.SetParameter("DestinationFolder", "$(OutDir)");
            copyProjectSettingsTask.Condition = "'$(Configuration)'=='Debug'";

            copyProjectSettingsTask = target.AddTask("Copy");
            copyProjectSettingsTask.SetParameter("SourceFiles", "@(ProjectSettingsFile)");
            copyProjectSettingsTask.SetParameter("DestinationFolder", "$(PublishDir)");
            copyProjectSettingsTask.Condition = "'$(Configuration)'=='Release'";

            return projectRoot;
        }

        private void AddCsFiles(IBuildAppSettings settings, IEnumerable<string> excludedDirectories)
        {
            var itemGroup = projectRoot.AddItemGroup();
            var item = itemGroup.AddItem("Compile", $"{settings.CacheSourceRelativeDir}/**/*.cs");
            item.Exclude = string.Join(';',
                ExcludePatterns(Path.GetDirectoryName(projectRoot.FullPath)!, excludedDirectories));
        }

        private ProjectRootElement AddAssemblyReferences(IBuildAppSettings settings, IEnumerable<string> assemblyNames)
        {
            var projectDirectory = Path.GetDirectoryName(projectRoot.FullPath)!;
            var itemGroup = projectRoot.AddItemGroup();
            foreach (var name in assemblyNames)
                itemGroup.AddItem("ProjectReference",
                    Path.GetRelativePath(projectDirectory, AssemblyCsProjFullPath(settings, name)));
            return projectRoot;
        }

        private ProjectRootElement AddProjectInfo(IBuildAppSettings settings,
            bool executable,
            ExecutableGenerationMode mode = ExecutableGenerationMode.Export)
        {
            var group = projectRoot.AddPropertyGroup();
            group.AddProperty("OutputType", executable ? "WinExe" : "Library");
            group.AddProperty("TargetFramework", settings.TargetFramework);
            group.AddProperty("ImplicitUsings", "disable");
            group.AddProperty("Nullable", "enable");

            if (executable)
            {
                group.AddProperty("AllowUnsafeBlocks", "true");

                var icon = Path.Combine(settings.CacheAbsoluteDir, ProjectIcon.IcoFileName);
                if (File.Exists(icon)) group.AddProperty("ApplicationIcon", icon);

                if (mode == ExecutableGenerationMode.Export)
                {
                    group.AddProperty("DebugType", "None");
                    group.AddProperty("DebugSymbols", "false");
                    group.AddProperty("PublishSingleFile", "true");
                    group.AddProperty("SelfContained", "true");
                }
                else
                {
                    group.AddProperty("DebugType", "portable");
                    group.AddProperty("DebugSymbols", "true");
                    group.AddProperty("PublishSingleFile", "false");
                    group.AddProperty("SelfContained", "false");
                }
            }
            else
            {
                group.AddProperty("GenerateDocumentationFile", "true");
                // The XML docs feed inspector tooltips; they are optional, so undocumented members are not warned about.
                group.AddProperty("NoWarn", "$(NoWarn);CS1591");
            }

            return projectRoot;
        }

        private ProjectRootElement AddUserProject(IBuildAppSettings settings)
        {
            var itemGroup = projectRoot.AddItemGroup();
            itemGroup.AddItem("ProjectReference",
                Path.GetRelativePath(Path.GetDirectoryName(projectRoot.FullPath)!, UserCodeCsProjFullPath(settings)));

            // Definitions the default assembly does not reference would otherwise be left out of the game.
            var graph = DiscoverAssemblies(settings);
            return projectRoot.AddAssemblyReferences(settings,
                graph.Definitions.Where(static a => !a.EditorOnly).Select(static a => a.Name)
                    .Except(graph.Default.References));
        }

        private ProjectRootElement AddInternalDllReferences(IBuildAppSettings settings)
        {
            var itemGroup = projectRoot.AddItemGroup();
            var usesPublishedLibraries = IsPublishedDistribution();
            foreach (var turianPackage in settings.TurianPackages)
            {
                itemGroup.AddItem("Reference", turianPackage.Item2)
                    .AddMetadata("HintPath",
                        usesPublishedLibraries
                            ? Path.Combine("$(TurianDir)", "lib", $"{turianPackage.Item2}.dll")
                            : Path.Combine(
                                "$(TurianDir)",
                                turianPackage.Item1,
                                "bin",
                                "Debug",
                                settings.TargetFramework,
                                $"{turianPackage.Item2}.dll"),
                        true);
            }

            // Generates reflection-free serializers and [Observable] properties for the project's DataAssets.
            itemGroup.AddItem("Analyzer",
                usesPublishedLibraries
                    ? Path.Combine("$(TurianDir)", "lib", $"{codeGeneratorName}.dll")
                    : Path.Combine("$(TurianDir)", "Turian", "Editor", "CSharp", "CodeGenerator", "bin", "Debug",
                        "netstandard2.0", $"{codeGeneratorName}.dll"));

            return projectRoot;
        }

        private ProjectRootElement AddNugetPackageReferences(IBuildAppSettings settings)
        {
            var itemGroup = projectRoot.AddItemGroup();
            foreach (var package in settings.PackageReferences)
            {
                itemGroup.AddItem("PackageReference", package.Item1)
                    .AddMetadata("Version", package.Item2, true);
            }
            return projectRoot;
        }
    }

    static void CopyPlatformSpecificFiles(this ProjectRootElement projectRoot, string platform = "win-x64")
    {
        var publishedPath = Path.Combine(AppContext.BaseDirectory, "lib", "Platform", platform);
        var fullPath = Directory.Exists(publishedPath)
            ? publishedPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Platform", platform));

        var files = Directory.GetFiles(fullPath);
        var generatedProjectDirectory = Path.GetDirectoryName(projectRoot.FullPath)
            ?? throw new InvalidOperationException("Could not determine generated project directory.");
        var destDir = generatedProjectDirectory;

        if (Directory.Exists(destDir))
        {
            foreach (var existingFile in Directory.GetFiles(destDir))
            {
                File.Delete(existingFile);
            }
        }
        else
        {
            Directory.CreateDirectory(destDir);
        }

        // Copy each file to the target directory
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(destDir, fileName);
            File.Copy(file, destFile, true);
        }
    }
}
