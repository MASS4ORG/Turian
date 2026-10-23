namespace Turian.Editor.Core;

/// <summary>How serious a <see cref="ProjectIssue"/> is.</summary>
public enum ProjectIssueSeverity
{
    /// <summary>The project opens, but something in it is ignored or ambiguous.</summary>
    Warning,

    /// <summary>The folder cannot be opened as a project.</summary>
    Error,
}

/// <summary>Something wrong with a project folder.</summary>
/// <param name="Severity">Whether the project can still be opened.</param>
/// <param name="Message">What is wrong, naming the files involved.</param>
public sealed record ProjectIssue(ProjectIssueSeverity Severity, string Message);

/// <summary>
/// Checks a folder before it is opened as a project. A folder is a project when it holds an <c>Assets</c> folder;
/// the rest is about its <see cref="ProjectAsset"/>: the engine version that saved it, and the settings it
/// registers, which can be missing, registered twice or unreadable. A project not yet given a project asset is
/// checked for the duplicates a search of its settings would meet.
/// </summary>
public static class ProjectValidator
{
    /// <summary>Checks a project folder.</summary>
    /// <param name="path">A project folder, or a file inside one.</param>
    /// <returns>The issues found, errors first; empty for a sound project.</returns>
    public static IReadOnlyList<ProjectIssue> Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(Directory.Exists(path) || File.Exists(path)))
        {
            return [new(ProjectIssueSeverity.Error, $"{path} does not exist.")];
        }

        if (SettingsService.ResolveProjectDirectory(path) is not { } directory)
        {
            return [new(ProjectIssueSeverity.Error, $"{path} is not a project: it has no Assets folder.")];
        }

        var assets = Path.Combine(directory, SettingsService.AssetsFolderName);
        var issues = ProjectAssetIssues(directory, assets);
        var sources = ProjectSettingsLoader.FindSources(assets);

        foreach (var kind in sources.GroupBy(static source => source.Kind).Where(static group => group.Count() > 1))
        {
            var used = kind.First().SourcePath;
            var ignored = string.Join(", ", kind.Skip(1).Select(source => Relative(directory, source.SourcePath)));
            issues.Add(new(ProjectIssueSeverity.Warning,
                $"{kind.Key.Name}: {Relative(directory, used)} is used; {ignored} ignored."));
        }

        foreach (var source in sources.DistinctBy(static source => source.Kind))
        {
            try
            {
                if (DataAsset.LoadContent(source.SourcePath, database: null) is not ProjectSettingsAsset)
                {
                    issues.Add(new(ProjectIssueSeverity.Warning,
                        $"{Relative(directory, source.SourcePath)} could not be read as {source.Kind.Name}."));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                issues.Add(new(ProjectIssueSeverity.Warning,
                    $"{Relative(directory, source.SourcePath)} could not be read: {ex.Message}"));
            }
        }

        return [.. issues.OrderByDescending(static issue => issue.Severity)];
    }

    // The project asset's own issues: the engine version that saved it, and registered settings without a file.
    static List<ProjectIssue> ProjectAssetIssues(string directory, string assets)
    {
        if (ProjectAsset.Load(assets) is not { } project)
        {
            return [];
        }

        var index = new AssetSourceIndex(directory);
        List<ProjectIssue> issues = [.. project.Settings.Where(r => !r.IsEmpty && index.PathOf(r.AssetId) is null)
            .Select(r => new ProjectIssue(ProjectIssueSeverity.Warning,
                $"{ProjectAsset.FileName} registers settings {r.AssetId}, which has no file."))];
        if (EngineVersionIssue(project) is { } versionIssue)
        {
            issues.Insert(0, versionIssue);
        }

        return issues;
    }

    /// <summary>
    /// How the engine that saved the project compares with this one: a newer major version cannot be opened, a
    /// newer minor one may hold data this engine drops, and an older one is upgraded when opened.
    /// </summary>
    static ProjectIssue? EngineVersionIssue(ProjectAsset project)
    {
        if (!MASS4.Bricks.SemanticVersion.TryParse(project.EngineVersion, out var saved))
        {
            return null;
        }

        var current = ProjectPackages.EngineVersion;
        if (saved.Major > current.Major)
        {
            return new(ProjectIssueSeverity.Error,
                $"The project was saved by Turian {saved}; this is Turian {current}. "
                + $"Open it with Turian {saved.Major} or newer.");
        }

        if (saved > current)
        {
            return new(ProjectIssueSeverity.Warning,
                $"The project was saved by the newer Turian {saved}; "
                + $"what this Turian {current} does not know is lost when saving.");
        }

        return saved < current
            ? new(ProjectIssueSeverity.Warning,
                $"The project was saved by Turian {saved}; opening it upgrades it to Turian {current}.")
            : null;
    }

    /// <summary>Logs each issue at its severity.</summary>
    /// <param name="issues">The issues to report.</param>
    /// <param name="log">Where they go.</param>
    /// <returns>True when none of them is an error.</returns>
    public static bool Report(IReadOnlyList<ProjectIssue> issues, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(log);

        foreach (var issue in issues)
        {
            if (issue.Severity == ProjectIssueSeverity.Error)
            {
                log.LogError("Project: {Message}", issue.Message);
            }
            else
            {
                log.LogWarning("Project: {Message}", issue.Message);
            }
        }

        return issues.All(static issue => issue.Severity != ProjectIssueSeverity.Error);
    }

    static string Relative(string directory, string path) => Path.GetRelativePath(directory, path);
}
