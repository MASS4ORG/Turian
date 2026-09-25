namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the build process.
/// </summary>
sealed partial class Build
{
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
            PublishDirectory.DeleteDirectory();
            CoverageDirectory.DeleteDirectory();
        });

    Target Restore => td => td
        .DependsOn(Clean)
        .Executes(() =>
        {
            _ = DotNetRestore(s => s.SetProjectFile(Solution));
        });

    Target Compile => td => td
        .DependsOn(CompileShaders)
        .After(Restore)
        .Executes(() =>
        {
            Log.Debug("Configuration {Configuration}", Configuration);

            DotNetBuild(settings => settings
                .SetNoLogo(true)
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
            );

            var studioOutputDirectory = Solution.Editor.Turian_Editor_Studio.Directory / "bin" / Configuration / "net10.0";
            var platformSourceDirectory = Solution.Editor.Turian_Editor_CLI.Directory / "Platform";
            var platformTargetDirectory = studioOutputDirectory / "Platform";

            Log.Information("Copying platform assets from {Source} to {Target}", platformSourceDirectory, platformTargetDirectory);

            platformSourceDirectory.CreateDirectory();
            platformTargetDirectory.DeleteDirectory();
            platformSourceDirectory.CopyToDirectory(studioOutputDirectory);
        });
}
