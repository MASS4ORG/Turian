namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the build process.
/// </summary>
sealed partial class Build
{
    /// <summary>
    /// Builds against the published Guinevere packages even when Directory.Build.local.props points at a local
    /// checkout; switching modes needs a fresh restore.
    /// </summary>
    [Parameter("Build against the published Guinevere packages, ignoring a local checkout")]
    readonly bool GuineverePackages;

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
                .SetProperty("GuinevereUsePackages", GuineverePackages));
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
                .SetProperty("GuinevereUsePackages", GuineverePackages)
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
