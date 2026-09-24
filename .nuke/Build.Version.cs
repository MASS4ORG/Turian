namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the versioning using GitVersion.
/// </summary>
sealed partial class Build
{
    // NoFetch: GitVersion's own libgit2-based fetch doesn't support SSH remotes, which silently
    // fails injection (gitVersion stays null) when the repo's remotes are SSH URLs. The checkout
    // (local or CI) already has the history/tags it needs, so no additional fetch is required.
    [GitVersion(NoFetch = true)]
    readonly GitVersion gitVersion;

    /// <summary>
    /// The current version, using GitVersion.
    /// </summary>
    string Version => gitVersion.MajorMinorPatch;

    public string VersionMajor => $"{gitVersion.Major}";

    public string VersionMajorMinor => $"{gitVersion.Major}.{gitVersion.Minor}";

    /// <summary>
    /// The version in a format that can be used as a tag.
    /// </summary>
    string TagName => $"v{Version}";

    /// <summary>
    /// Checks if there are new commits since the last tag.
    /// </summary>
    bool HasNewCommits => gitVersion.CommitsSinceVersionSource != "0";

    string currentVersion;
    string CurrentTag
    {
        get
        {
            if (currentVersion is null)
            {
                try
                {
                    currentVersion = Git("describe --tags --abbrev=0")
                        .FirstOrDefault()
                        .Text;
                }
                catch
                {
                    currentVersion = "0.0.0";
                }
            }
            return currentVersion;
        }
    }
    string CurrentVersion => CurrentTag.TrimStart('v');

    /// <summary>
    /// Whether the repository has at least one tag yet (false on the very first release).
    /// </summary>
    bool HasAnyTags => Git("tag", logOutput: false).Any();

    /// <summary>
    /// Prints the current version.
    /// </summary>
    Target ShowCurrentVersion => td =>
        td
            .Executes(() =>
            {
                // var lastCommmit = GitTasks.Git("log -1").FirstOrDefault().Text;
                // var status = GitTasks.Git("status").FirstOrDefault().Text;
                Log.Information("Current version:\t\t{Version}", CurrentVersion);
                Log.Information("Current tag:\t\t{Version}", CurrentTag);
                Log.Information("Next version:\t\t{Version}", Version);
            });

    /// <summary>
    /// Checks if there are new commits since the last tag.
    /// If there are no new commits, the whole publish process is skipped.
    /// </summary>
    public Target CheckNewCommits => td =>
        td
            .DependsOn(ShowCurrentVersion)
            .Executes(() =>
            {
                Log.Information("Next version:\t\t{Version}", TagName);
                Log.Information("Checking for new commits...");

                // If there are no new commits since the last tag, skip tag creation
                // Nuke will stop here and not execute any of the following targets
                if (HasNewCommits)
                {
                    Log.Information(
                        "There are {GitVersionCommitsSinceVersionSource} new commits since last tag",
                        gitVersion.CommitsSinceVersionSource
                    );
                }
                else
                {
                    Log.Information("No new commits since last tag. Skipping tag creation");
                }
            });
}
