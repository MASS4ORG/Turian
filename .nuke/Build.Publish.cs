namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the publish process.
/// </summary>
sealed partial class Build
{
    [Parameter(
        "Runtime identifier for the build (e.g., win-x64, linux-x64, osx-x64) (default: linux-x64)"
    )]
    readonly string runtimeIdentifier = "linux-x64";

    [Parameter("publish-directory (default: ./.publish/{runtimeIdentifier})")]
    readonly AbsolutePath publishDirectory;
    AbsolutePath PublishDirectory =>
        publishDirectory ?? RootDirectory / ".publish" / runtimeIdentifier;

    [Parameter("publish-self-contained (default: false)")]
    readonly bool publishSelfContained;

    [Parameter("publish-single-file (default: false)")]
    readonly bool publishSingleFile;

    [Parameter("publish-trimmed (default: false)")]
    readonly bool publishTrimmed;

    static readonly string[] releaseProjectNames = ["Turian.Editor.CLI", "Turian.Editor.Studio"];

    static readonly (string Directory, string Name)[] engineLibraries =
    [
        ("Turian/Engine/Attributes", "Turian.Engine.Attributes"),
        ("Turian/Engine/Core", "Turian.Engine.Core"),
        ("Turian/Engine/UI", "Turian.Engine.UI")
    ];

    public Target Publish => td =>
        td
            .DependsOn(Restore)
            .Executes(() =>
            {
                var libraryDirectory = PublishDirectory / "lib";
                libraryDirectory.CreateOrCleanDirectory();
                foreach (var project in Solution.AllProjects)
                {
                    // Release archives contain the two runnable products. The solution also has test and
                    // build-host executables, which are development tools and must not ship as releases.
                    if (!releaseProjectNames.Contains(project.Name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    _ = DotNetPublish(
                        settings =>
                            settings
                                .SetProject(project)
                                .SetNoLogo(true)
                                .SetConfiguration(Configuration)
                                .SetOutput(libraryDirectory)
                                .SetRuntime(runtimeIdentifier)
                                .SetSelfContained(publishSelfContained)
                                .SetPublishSingleFile(publishSingleFile)
                                .SetPublishTrimmed(publishTrimmed)
                                .SetProperty("UseAppHost", "false")
                                .SetProperty("SatelliteResourceLanguages", "en")
                                .SetProperty("GuineverePackageExcludeAssets", "none")    // Needed to build locally with prod config
                                .AddProcessAdditionalArguments("-p:GuinevereLocalPath=") // Needed to build locally with prod config
                                .SetAuthors("Bruno Massa")
                                .SetVersion(CurrentVersion)
                                .SetAssemblyVersion(CurrentVersion)
                                .SetInformationalVersion(CurrentVersion)
                    );
                }

                PublishDirectory.GlobFiles("**/*.pdb", "**/*.xml").ForEach(file => file.DeleteFile());

                foreach (var (directory, name) in engineLibraries)
                {
                    var source = RootDirectory / directory / "bin" / Configuration / "net10.0" / $"{name}.dll";
                    source.CopyToDirectory(libraryDirectory, ExistsPolicy.FileOverwrite);
                }

                PublishBootstrap("turian-cli");
                PublishBootstrap("turian-studio");

                PublishDirectory.GlobDirectories("**/*")
                    .OrderByDescending(directory => directory.ToString().Length)
                    .Where(directory => !Directory.EnumerateFileSystemEntries(directory).Any())
                    .ForEach(directory => directory.DeleteDirectory());
            });

    void PublishBootstrap(string launcherName)
    {
        var output = PublishDirectory / ".bootstrap";
        output.CreateOrCleanDirectory();
        _ = DotNetPublish(settings => settings
            .SetProject(Solution.Editor.Turian_Editor_Bootstrap)
            .SetConfiguration(Configuration)
            .SetOutput(output)
            .SetRuntime(runtimeIdentifier)
            .SetSelfContained(false)
            .SetPublishSingleFile(true)
            // Studio is a GUI app: as a console app it would open a console window beside it on Windows. Each
            // launcher builds in its own intermediate folder, or the second would reuse the first's app host.
            .SetProperty("OutputType", launcherName == "turian-studio" ? "WinExe" : "Exe")
            .SetProperty("IntermediateOutputPath", $"obj/{launcherName}/"));
        var extension = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        (output / $"Turian.Editor.Bootstrap{extension}")
            .Move(PublishDirectory / $"{launcherName}{extension}", ExistsPolicy.FileOverwrite);
        output.DeleteDirectory();
    }

    void RenameExecutable(string sourceName, string destinationName)
    {
        var extension = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        var source = PublishDirectory / $"{sourceName}{extension}";
        if (!source.FileExists())
        {
            return;
        }

        source.Move(PublishDirectory / $"{destinationName}{extension}", ExistsPolicy.FileOverwrite);
    }
}
