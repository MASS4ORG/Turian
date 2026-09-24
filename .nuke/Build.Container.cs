namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for building/pushing the Turian CLI container image
/// gamedevs use to build their projects in their own CI/CD. Uses Podman.
/// </summary>
sealed partial class Build
{
    [Parameter("Comma-separated image repositories to tag/push, e.g. ghcr.io/mass4org/turian-cli")]
    readonly string containerRegistries = "";

    string[] ContainerRegistries =>
        containerRegistries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static AbsolutePath ContainerFile => RootDirectory / "Containerfile";

    string ContainerLocalTag => $"turian-cli:{CurrentVersion}";

    public Target ContainerBuild => td =>
        td
            .Executes(() => RunPodman($"build -f \"{ContainerFile}\" -t {ContainerLocalTag} --build-arg VERSION={CurrentVersion} \"{RootDirectory}\""));

    /// <summary>
    /// Tags and pushes the image built by <see cref="ContainerBuild"/> to every repository listed in
    /// <see cref="ContainerRegistries"/>, as both the release version and "latest". The CI job is
    /// responsible for `podman login` against each registry beforehand.
    /// </summary>
    public Target ContainerPush => td =>
        td
            .DependsOn(ContainerBuild)
            .Requires(() => containerRegistries)
            .Executes(() =>
            {
                foreach (var repository in ContainerRegistries)
                {
                    foreach (var tag in new[] { CurrentVersion, "latest" })
                    {
                        var remoteTag = $"{repository}:{tag}";
                        RunPodman($"tag {ContainerLocalTag} {remoteTag}");
                        RunPodman($"push {remoteTag}");
                        Log.Information("Pushed {RemoteTag}", remoteTag);
                    }
                }
            });

    static void RunPodman(string arguments) =>
        ProcessTasks.StartProcess("podman", arguments).AssertZeroExitCode();
}
