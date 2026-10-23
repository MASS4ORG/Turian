namespace Build;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for building/pushing the Turian CLI container image
/// gamedevs use to build their projects in their own CI/CD. Uses Podman.
/// </summary>
sealed partial class Build
{
    [Parameter("Comma-separated image repositories to tag/push, e.g. ghcr.io/mass4org/turian-cli")]
    readonly string ContainerRegistries = "";

    string[] ContainerAllRegistries =>
        ContainerRegistries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static AbsolutePath ContainerFile => RootDirectory / "Containerfile";

    string ContainerLocalTag => $"turian-cli:{CurrentVersion}";

    public Target ContainerBuild => td =>
        td
            .Executes(() => RunPodman($"build -f \"{ContainerFile}\" -t {ContainerLocalTag} --build-arg VERSION={CurrentVersion} \"{RootDirectory}\""));

    /// <summary>
    /// Tags and pushes the image built by <see cref="ContainerBuild"/> to every repository listed in
    /// <see cref="ContainerAllRegistries"/>, as both the release version and "latest". A version tag is never
    /// pushed twice: brick releases resolve it to a digest, so moving it would change what a re-run packs with.
    /// The CI job is responsible for `podman login` against each registry beforehand.
    /// </summary>
    public Target ContainerPush => td =>
        td
            .DependsOn(ContainerBuild)
            .Requires(() => ContainerRegistries)
            .Executes(() =>
            {
                foreach (var repository in ContainerAllRegistries)
                {
                    AssertVersionTagIsNew(repository, CurrentVersion);
                }

                foreach (var repository in ContainerAllRegistries)
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

    /// <summary>
    /// Fails when the repository already holds the version's tag, and when it cannot tell whether it does. A
    /// repository the registry does not know yet holds no tags.
    /// </summary>
    static void AssertVersionTagIsNew(string repository, string version)
    {
        var search = ProcessTasks.StartProcess("podman",
            $"search --list-tags --limit 100000 --format {{{{.Tag}}}} {repository}", logOutput: false);
        search.WaitForExit();
        var lines = search.Output.Select(static line => line.Text).ToList();
        if (search.ExitCode != 0)
        {
            if (lines.Any(static line => line.Contains("NAME_UNKNOWN", StringComparison.OrdinalIgnoreCase)
                                         || line.Contains("not known to registry", StringComparison.Ordinal)))
            {
                return;
            }

            throw new InvalidOperationException($"Could not list the tags of {repository}: {string.Join(' ', lines)}");
        }

        if (lines.Contains(version, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{repository}:{version} is already published and version tags are immutable; release a new version instead.");
        }
    }

    static void RunPodman(string arguments) =>
        ProcessTasks.StartProcess("podman", arguments).AssertZeroExitCode();
}
