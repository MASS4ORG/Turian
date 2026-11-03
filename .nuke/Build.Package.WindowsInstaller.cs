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
            var script = BuildProjectDirectory / "packaging" / "windows" / $"{ProductName}.nsi";
            ProcessTasks.StartProcess(FindMakeNsis(),
                    $"-V2 -DVERSION={Version} -DSOURCE_DIR=\"{PublishDir}\" " +
                    $"-DOUTPUT_FILE=\"{WindowsInstallerFile}\" \"{script}\"")
                .AssertZeroExitCode();
        });

    /// <summary>
    /// Finds makensis on the PATH or in the default NSIS folder, which the Windows NSIS setup does not add to the PATH.
    /// </summary>
    static string FindMakeNsis() =>
        FindOnPath("makensis")
        ?? new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(Environment.GetFolderPath)
            .Where(static folder => folder.Length > 0)
            .Select(static folder => Path.Combine(folder, "NSIS", "makensis.exe"))
            .FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("makensis was not found: install NSIS (apt install nsis, choco install nsis).");

    /// <summary>Returns the full path of an executable found on the PATH, or null when it is missing.</summary>
    static string FindOnPath(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder, fileName))
            .FirstOrDefault(File.Exists);
    }
}
