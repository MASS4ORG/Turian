using System.Xml.Linq;

namespace Turian.Editor.Core;

/// <summary>The files a regeneration produced.</summary>
/// <param name="Solution">The solution an external IDE opens.</param>
/// <param name="Projects">The generated projects the solution lists: assembly definitions first, the default assembly last.</param>
/// <param name="Changed">Whether any file was written; <c>false</c> when everything already matched the project's state.</param>
public sealed record GeneratedProjectFiles(string Solution, IReadOnlyList<string> Projects, bool Changed);

/// <summary>
/// Rebuilds the generated user-code projects and the solution that lists them, the way a compile does, without
/// compiling. Files whose content is unchanged are left untouched, so their timestamps and any IDE state survive.
/// </summary>
public static class ProjectRegeneration
{
    /// <summary>The solution's path: <c>&lt;title&gt;.slnx</c> in the project's cache folder.</summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The full path, which may not exist yet.</returns>
    public static string SolutionPath(IBuildAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Path.Combine(settings.CacheAbsoluteDir, $"{settings.TitleToPathFriendly}.slnx");
    }

    /// <summary>
    /// Regenerates one project per assembly definition, the default assembly's project and the solution, from the
    /// project's current bricks, packages, assembly definitions and active build profile.
    /// </summary>
    /// <param name="settings">The settings of the open project.</param>
    /// <param name="logger">Where problems are reported.</param>
    /// <returns>The solution and project paths.</returns>
    public static GeneratedProjectFiles Regenerate(IAppSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var build = settings as BuildAppSettings ?? (BuildAppSettings)new BuildAppSettings().Load(settings);
        var projects = CsProjectGenerator.GenerateUserCodeProjects(build, logger,
            CsProjectGenerator.DiscoverAssemblies(build));
        var changed = false;
        foreach (var project in projects)
        {
            changed |= GeneratedProject.Save(project);
        }

        var paths = projects.Select(static project => project.FullPath).ToList();
        var solution = SolutionPath(build);
        changed |= GeneratedProject.SaveText(solution, SolutionXml(solution, paths));
        return new GeneratedProjectFiles(solution, paths, changed);
    }

    /// <summary>The <c>.slnx</c> text listing <paramref name="projects"/> with paths relative to the solution.</summary>
    /// <param name="solution">The solution file's full path.</param>
    /// <param name="projects">The projects' full paths.</param>
    /// <returns>The solution file's content.</returns>
    public static string SolutionXml(string solution, IEnumerable<string> projects)
    {
        var directory = Path.GetDirectoryName(solution)!;
        var root = new XElement("Solution", projects.Select(project =>
            new XElement("Project", new XAttribute("Path",
                Path.GetRelativePath(directory, project).Replace('\\', '/')))));
        return root + "\n";
    }
}
