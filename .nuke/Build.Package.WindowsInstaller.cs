namespace Build;

/// <summary>
/// Builds the Windows installer included with regular Windows releases.
/// See .nuke/packaging/windows/README.md before publishing an installer.
/// </summary>
sealed partial class Build
{
    AbsolutePath WindowsInstallerFile =>
        ArtifactsDirectory / $"{ProductName}-{Version}-win-x64-setup.exe";

    public Target WindowsInstaller => td => td
        .DependsOn(Publish)
        .OnlyWhenStatic(() => RuntimeIdentifier == "win-x64")
        .Executes(() =>
        {
            ArtifactsDirectory.CreateDirectory();
            WindowsInstallerFile.DeleteFile();
            var script = BuildProjectDirectory / "packaging" / "windows" / "Turian.nsi";
            ProcessTasks.StartProcess("makensis",
                    $"-V2 -DVERSION={Version} -DSOURCE_DIR=\"{PublishDir}\" " +
                    $"-DOUTPUT_FILE=\"{WindowsInstallerFile}\" \"{script}\"")
                .AssertZeroExitCode();
        });

}
