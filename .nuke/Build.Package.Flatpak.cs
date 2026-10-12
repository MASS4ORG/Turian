namespace Build;

/// <summary>Builds a local Flatpak bundle for the Studio editor.</summary>
sealed partial class Build
{
    const string flatpakAppId = "org.MASS4.Turian";
    AbsolutePath FlatpakManifest => BuildProjectDirectory / "packaging" / "flatpak" / $"{flatpakAppId}.yml";
    AbsolutePath FlatpakBuildDirectory => RootDirectory / ".flatpak-build";
    AbsolutePath FlatpakRepository => RootDirectory / ".flatpak-repo";
    AbsolutePath FlatpakBundle => ArtifactsDirectory / $"{ProductName}-{Version}-x86_64.flatpak";

    public Target Flatpak => td => td
        .Executes(() =>
        {
            FlatpakBuildDirectory.DeleteDirectory();
            FlatpakRepository.CreateDirectory();
            ArtifactsDirectory.CreateDirectory();
            FlatpakBundle.DeleteFile();
            ProcessTasks.StartProcess("flatpak-builder",
                    $"--force-clean --repo=\"{FlatpakRepository}\" --install-deps-from=flathub " +
                    $"\"{FlatpakBuildDirectory}\" \"{FlatpakManifest}\"")
                .AssertZeroExitCode();
            ProcessTasks.StartProcess("flatpak",
                    $"build-bundle \"{FlatpakRepository}\" \"{FlatpakBundle}\" {flatpakAppId}")
                .AssertZeroExitCode();
        });
}
