namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the build process.
/// </summary>
sealed partial class Build
{
    /// <summary>
    /// Builds against the published packages even when Directory.Build.local.props points at local checkouts;
    /// switching modes needs a fresh restore. Pack and publish refuse or warn without it (Directory.Build.targets).
    /// </summary>
    [Parameter("Build against the published packages, ignoring local checkouts from Directory.Build.local.props")]
    readonly bool NoLocalPackages;

    Target Clean => td => td
        .Executes(() =>
        {
            Solution.AllProjects
                .Where(project => project.Path != RootDirectory / ".nuke/Build.csproj")
                .SelectMany(project => new[]
                {
                    project.Directory / "bin",
                    project.Directory / "obj",
                    project.Directory / "output",
                })
                .Distinct()
                .Where(path => path.DirectoryExists())
                .ForEach(path => path.DeleteDirectory());
            PublishDir.DeleteDirectory();
            CoverageDirectory.DeleteDirectory();
        });

    Target Restore => td => td
        .DependsOn(Clean)
        .Executes(() =>
        {
            _ = DotNetRestore(s => s
                .SetProjectFile(Solution)
                .SetProperty("NoLocalPackages", NoLocalPackages));
        });

    Target Compile => td => td
        .DependsOn(CompileShaders)
        .After(Restore)
        .Executes(() =>
        {
            Log.Debug("Config {Config}", Config);

            DotNetBuild(settings => settings
                .SetNoLogo(true)
                .SetProjectFile(Solution)
                .SetConfiguration(Config)
                .SetProperty("NoLocalPackages", NoLocalPackages)
                .SetProperty("SkipShaders", SkipShaders)
                .EnableNoRestore()
            );

            var studioOutputDirectory = Solution.Editor.Turian_Editor_Studio.Directory / "bin" / Config / "net10.0";
            var platformSourceDirectory = Solution.Editor.Turian_Editor_CLI.Directory / "Platform";
            var platformTargetDirectory = studioOutputDirectory / "Platform";

            Log.Information("Copying platform assets from {Source} to {Target}", platformSourceDirectory, platformTargetDirectory);

            platformSourceDirectory.CreateDirectory();
            platformTargetDirectory.DeleteDirectory();
            platformSourceDirectory.CopyToDirectory(studioOutputDirectory);
        });
}
