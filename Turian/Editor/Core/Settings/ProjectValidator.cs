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
/// Checks a folder before it is opened as a project. With no project file to vouch for it, a folder is a
/// project when it holds an <c>Assets</c> folder; the rest is about its settings assets, which nothing
/// lists and so can be duplicated or left unreadable without anyone noticing.
/// </summary>
public static class ProjectValidator
{
    /// <summary>Checks a project folder.</summary>
    /// <param name="path">A project folder, or a file inside one.</param>
    /// <returns>The issues found, errors first; empty for a sound project.</returns>
    public static IReadOnlyList<ProjectIssue> Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(Directory.Exists(path) || File.Exists(path)))
            return [new(ProjectIssueSeverity.Error, $"{path} does not exist.")];

        if (SettingsService.ResolveProjectDirectory(path) is not { } directory)
            return [new(ProjectIssueSeverity.Error, $"{path} is not a project: it has no Assets folder.")];

        var issues = new List<ProjectIssue>();
        var sources = ProjectSettingsLoader.FindSources(Path.Combine(directory, SettingsService.AssetsFolderName));

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
                if (DataAsset.LoadContent(source.SourcePath) is not ProjectSettingsAsset)
                    issues.Add(new(ProjectIssueSeverity.Warning,
                        $"{Relative(directory, source.SourcePath)} could not be read as {source.Kind.Name}."));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                issues.Add(new(ProjectIssueSeverity.Warning,
                    $"{Relative(directory, source.SourcePath)} could not be read: {ex.Message}"));
            }
        }

        return issues;
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
            if (issue.Severity == ProjectIssueSeverity.Error) log.LogError("Project: {Message}", issue.Message);
            else log.LogWarning("Project: {Message}", issue.Message);

        return issues.All(static issue => issue.Severity != ProjectIssueSeverity.Error);
    }

    static string Relative(string directory, string path) => Path.GetRelativePath(directory, path);
}
