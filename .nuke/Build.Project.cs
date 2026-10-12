namespace Build;

/// <summary>
/// The only build file that differs between the MASS4 repositories besides the feature partials: it names this
/// project and wires its features into the shared build. Names of projects, repositories and products live here.
/// </summary>
sealed partial class Build
{
    const string ProductName = "Turian";
    const string DefaultGithubRepository = "MASS4ORG/Turian";
    const string DefaultGitlabProjectPath = "MASS4ORG/Turian";
    const string ReleaseBotEmail = "massa+github@brunomassa.com";

    /// <summary>Every NuGet package ID must start with this; empty accepts any.</summary>
    const string NuGetPackagePrefix = "MASS4.";

    const bool DefaultPublishSelfContained = false;
    const bool DefaultPublishSingleFile = false;
    const bool DefaultPublishTrimmed = false;

    const string PackageAuthors = "Bruno Massa";
    const string PackageCopyrightOwner = "Bruno Massa";
    const string PackageProjectUrl = "https://turian.mass4.org/";
    const string PackageRepositoryUrl = "https://github.com/MASS4ORG/Turian.git";
    const string PackageTags = "game engine gamedev vulkan";

    /// <summary>The runnable products. The solution also has test and build-host executables that must not ship.</summary>
    IEnumerable<Project> ReleaseProjects => [Solution.Editor.Turian_Editor_CLI, Solution.Editor.Turian_Editor_Studio];

    Project TestProject => Solution.Turian_Tests;

    AbsolutePath PublishOutput(Project project) => PublishDir / "lib";

    [Parameter("Build against the published packages, ignoring local checkouts from Directory.Build.local.props")]
    readonly bool NoLocalPackages;

    /// <summary>
    /// Builds against the published packages even when Directory.Build.local.props points at local checkouts;
    /// switching modes needs a fresh restore. Pack and publish refuse or warn without it (Directory.Build.targets).
    /// </summary>
    Dictionary<string, object> MsBuildProperties => new()
    {
        ["NoLocalPackages"] = NoLocalPackages,
        ["SkipShaders"] = SkipShaders,
    };

    Target[] PackDependencies => [Publish, WindowsInstaller];

    Target[] ReleaseCommitDependencies => [UpdateProjectVersions];

    /// <summary>Bricks take their version from package.json rather than their project file.</summary>
    IEnumerable<Project> VersionedProjects => Solution.AllProjects.Where(project => !HasPackageManifest(project));

    const string ShaderSourceDirectoryName = "Turian/Packages/org.mass4.turian.vulkan/Source~/Turian.Engine.Vulkan";

    const string RuntimeAssembliesTarget = "CopyTurianRuntimeAssemblies";
    const string RuntimeLibraryDirectoryProperty = "TurianRuntimeLibraryDirectory";
    Project StudioProject => Solution.Editor.Turian_Editor_Studio;
    Project CliProject => Solution.Editor.Turian_Editor_CLI;
    Project BootstrapProject => Solution.Editor.Turian_Editor_Bootstrap;
    Project CodeGeneratorProject => Solution.Editor.CSharp.Turian_CSharp_CodeGenerator;
    Project RuntimeAssembliesProject => Solution.Editor.Platform.Turian_Editor_Build_Platform_Desktop;
    string CliLauncherName => CliProject.Name.Replace(".Editor.", "-", StringComparison.Ordinal).ToLowerInvariant();
    string StudioLauncherName => StudioProject.Name.Replace(".Editor.", "-", StringComparison.Ordinal).ToLowerInvariant();

    partial void ConfigurePublish(Project project, ref DotNetPublishSettings settings) =>
        settings = settings
            .SetProperty("UseAppHost", "false")
            .SetProperty("SatelliteResourceLanguages", "en")
            .SetAuthors("Bruno Massa");

    partial void OnCompiled() => CopyPlatformAssets();

    partial void OnPublished() => PublishEditorExtras();

    bool HasPackageManifest(Project project)
    {
        for (var directory = project.Directory; directory.ToString().StartsWith(RootDirectory, StringComparison.Ordinal); directory = directory.Parent)
        {
            if ((directory / "package.json").FileExists()) return true;
        }

        return false;
    }

    /// <summary>Copies the platform assets next to the Studio build, where the editor looks for them.</summary>
    void CopyPlatformAssets()
    {
        var studioOutputDirectory = StudioProject.Directory / "bin" / Config / "net10.0";
        var platformSourceDirectory = CliProject.Directory / "Platform";
        var platformTargetDirectory = studioOutputDirectory / "Platform";

        Log.Information("Copying platform assets from {Source} to {Target}", platformSourceDirectory, platformTargetDirectory);

        platformSourceDirectory.CreateDirectory();
        platformTargetDirectory.DeleteDirectory();
        platformSourceDirectory.CopyToDirectory(studioOutputDirectory);
    }

    void PublishEditorExtras()
    {
        var libraryDirectory = PublishDir / "lib";

        PublishDir.GlobFiles("**/*.pdb", "**/*.xml").ForEach(file => file.DeleteFile());
        (CodeGeneratorProject.Directory / "bin" / Config / "netstandard2.0" / "Turian.CSharp.CodeGenerator.dll")
            .CopyToDirectory(libraryDirectory, ExistsPolicy.FileOverwrite);

        _ = DotNetMSBuild(settings => settings
            .SetTargetPath(RuntimeAssembliesProject.Path)
            .SetTargets(RuntimeAssembliesTarget)
            .SetConfiguration(Config)
            .SetRestore(true)
            .SetNodeReuse(false)
            .SetMaxCpuCount(1)
            .SetProperty("NoLocalPackages", NoLocalPackages)
            .SetProperty("RuntimeIdentifier", RuntimeIdentifier)
            .SetProperty(RuntimeLibraryDirectoryProperty, libraryDirectory));

        PublishBootstrap(CliLauncherName);
        PublishBootstrap(StudioLauncherName);

        PublishDir.GlobDirectories("**/*")
            .OrderByDescending(directory => directory.ToString().Length)
            .Where(directory => !Directory.EnumerateFileSystemEntries(directory).Any())
            .ForEach(directory => directory.DeleteDirectory());
    }

    void PublishBootstrap(string launcherName)
    {
        var output = PublishDir / ".bootstrap";
        output.CreateOrCleanDirectory();
        _ = DotNetPublish(settings => settings
            .SetProject(BootstrapProject)
            .SetConfiguration(Config)
            .SetOutput(output)
            .SetRuntime(RuntimeIdentifier)
            .SetSelfContained(false)
            .SetPublishSingleFile(true)
            .SetProperty("OutputType", launcherName == StudioLauncherName ? "WinExe" : "Exe")
            .SetProperty("IntermediateOutputPath", $"obj/{launcherName}/"));
        var extension = RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        (output / $"{BootstrapProject.Name}{extension}").Move(PublishDir / $"{launcherName}{extension}", ExistsPolicy.FileOverwrite);
        output.DeleteDirectory();
    }
}
