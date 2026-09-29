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
    readonly string RuntimeIdentifier = "linux-x64";

    [Parameter("publish-directory (default: ./.publish/{runtimeIdentifier})")]
    readonly AbsolutePath PublishDirectory;

    AbsolutePath PublishDir => PublishDirectory ?? RootDirectory / ".publish" / RuntimeIdentifier;

    [Parameter("publish-self-contained (default: false)")]
    readonly bool PublishSelfContained;

    [Parameter("publish-single-file (default: false)")]
    readonly bool PublishSingleFile;

    [Parameter("publish-trimmed (default: false)")]
    readonly bool PublishTrimmed;

    static readonly string[] ReleaseProjectNames = ["Turian.Editor.CLI", "Turian.Editor.Studio"];

    static readonly (string Directory, string Name)[] EngineLibraries =
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
                var libraryDirectory = PublishDir / "lib";
                libraryDirectory.CreateOrCleanDirectory();
                foreach (var project in Solution.AllProjects)
                {
                    // Release archives contain the two runnable products. The solution also has test and
                    // build-host executables, which are development tools and must not ship as releases.
                    if (!ReleaseProjectNames.Contains(project.Name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    _ = DotNetPublish(settings =>
                        settings
                            .SetProject(project)
                            .SetNoLogo(true)
                            .SetConfiguration(Config)
                            .SetOutput(libraryDirectory)
                            .SetRuntime(RuntimeIdentifier)
                            .SetSelfContained(PublishSelfContained)
                            .SetPublishSingleFile(PublishSingleFile)
                            .SetPublishTrimmed(PublishTrimmed)
                            .SetProperty("UseAppHost", "false")
                            .SetProperty("SatelliteResourceLanguages", "en")
                            .SetProperty("NoLocalPackages", NoLocalPackages)
                            .SetAuthors("Bruno Massa")
                            .SetVersion(Version)
                            .SetAssemblyVersion(Version)
                            .SetInformationalVersion(Version)
                    );
                }

                PublishDir.GlobFiles("**/*.pdb", "**/*.xml").ForEach(file => file.DeleteFile());

                // The source generator user projects compile with (DataAsset serializers, [Observable]).
                (RootDirectory / "Turian/Editor/CSharp/CodeGenerator" / "bin" / Config / "netstandard2.0" /
                 "Turian.CSharp.CodeGenerator.dll").CopyToDirectory(libraryDirectory, ExistsPolicy.FileOverwrite);

                foreach (var (directory, name) in EngineLibraries)
                {
                    var source = RootDirectory / directory / "bin" / Config / "net10.0" / $"{name}.dll";
                    source.CopyToDirectory(libraryDirectory, ExistsPolicy.FileOverwrite);
                }

                PublishBootstrap("turian-cli");
                PublishBootstrap("turian-studio");

                PublishDir.GlobDirectories("**/*")
                    .OrderByDescending(directory => directory.ToString().Length)
                    .Where(directory => !Directory.EnumerateFileSystemEntries(directory).Any())
                    .ForEach(directory => directory.DeleteDirectory());
            });

    void PublishBootstrap(string launcherName)
    {
        var output = PublishDir / ".bootstrap";
        output.CreateOrCleanDirectory();
        _ = DotNetPublish(settings => settings
            .SetProject(Solution.Editor.Turian_Editor_Bootstrap)
            .SetConfiguration(Config)
            .SetOutput(output)
            .SetRuntime(RuntimeIdentifier)
            .SetSelfContained(false)
            .SetPublishSingleFile(true)
            // Studio is a GUI app: as a console app it would open a console window beside it on Windows. Each
            // launcher builds in its own intermediate folder, or the second would reuse the first's app host.
            .SetProperty("OutputType", launcherName == "turian-studio" ? "WinExe" : "Exe")
            .SetProperty("IntermediateOutputPath", $"obj/{launcherName}/"));
        var extension = RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        (output / $"Turian.Editor.Bootstrap{extension}")
            .Move(PublishDir / $"{launcherName}{extension}", ExistsPolicy.FileOverwrite);
        output.DeleteDirectory();
    }
}
