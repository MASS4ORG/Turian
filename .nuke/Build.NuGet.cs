namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for packing and pushing the NuGet libraries: the Turian engine
/// assemblies and the Gaya platform.
/// </summary>
sealed partial class Build
{
    [Parameter("NuGet API key used by PushNuGet. Absent = pack only.")]
    readonly string NugetApiKey;

    [Parameter("NuGet feed to push to (default: nuget.org)")]
    readonly string NugetSource = "https://api.nuget.org/v3/index.json";

    static AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";

    /// <summary>Every project declaring a PackageId. The rest of the solution is apps, tests and tooling.</summary>
    IEnumerable<Project> PackableProjects =>
        Solution.AllProjects.Where(project =>
            project.Path.FileExists() && project.Path.ReadAllText().Contains("<PackageId>", StringComparison.Ordinal));

    /// <summary>
    /// Gaya carries its own version (Gaya/Directory.Build.props) because its compatibility promise is
    /// to plugin authors, not to Turian releases. Turian's libraries follow GitVersion.
    /// </summary>
    static bool IsGayaProject(Project project) =>
        project.Path.ToString().Contains($"{Path.DirectorySeparatorChar}Gaya{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

    public Target PackNuGet => td =>
        td
            .DependsOn(Compile)
            .Executes(() =>
            {
                PackagesDirectory.CreateOrCleanDirectory();

                foreach (var project in PackableProjects)
                {
                    Log.Information("Packing {Project}", project.Name);

                    _ = DotNetPack(settings =>
                    {
                        settings = settings
                            .SetProject(project)
                            .SetNoLogo(true)
                            .SetConfiguration(Configuration)
                            .SetOutputDirectory(PackagesDirectory)
                            .EnableNoBuild()
                            .SetProperty("GuineverePackageExcludeAssets", "none")
                            .AddProcessAdditionalArguments("-p:GuinevereLocalPath=");

                        // Gaya's version comes from its own props; overriding it here would tie the
                        // SDK's compatibility promise back to Turian's release cadence.
                        return IsGayaProject(project)
                            ? settings
                            : settings.SetVersion(Version).SetAssemblyVersion(Version).SetInformationalVersion(Version);
                    });
                }

                Log.Information("Packed {Count} packages into {Directory}",
                    PackagesDirectory.GlobFiles("*.nupkg").Count, PackagesDirectory);
            });

    /// <summary>
    /// Pushes with --skip-duplicate, so re-running a release for an existing version is a no-op
    /// rather than an error.
    /// </summary>
    public Target PushNuGet => td =>
        td
            .DependsOn(PackNuGet)
            .Requires(() => NugetApiKey)
            .Executes(() =>
            {
                foreach (var package in PackagesDirectory.GlobFiles("*.nupkg"))
                {
                    Log.Information("Pushing {Package} to {Source}", package.Name, NugetSource);
                    _ = DotNetNuGetPush(settings => settings
                        .SetTargetPath(package)
                        .SetSource(NugetSource)
                        .SetApiKey(NugetApiKey)
                        .EnableSkipDuplicate());
                }
            });
}
