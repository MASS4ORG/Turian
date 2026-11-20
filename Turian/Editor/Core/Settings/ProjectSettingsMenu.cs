namespace Turian.Editor.Core;

/// <summary>One settings asset the Project Settings menu opens.</summary>
/// <param name="Kind">The concrete settings type the asset holds.</param>
/// <param name="Label">What the menu shows.</param>
public sealed record ProjectSettingsEntry(Type Kind, string Label)
{
    /// <summary>The id of the command that opens this entry.</summary>
    public string CommandId => $"gaya.turian.projectSettings.{Kind.FullName}";
}

/// <summary>
/// Lists the settings the open project actually has, as its project asset registers them, so the Project Settings menu
/// follows the project instead of a fixed list.
/// </summary>
public static partial class ProjectSettingsMenu
{
    /// <summary>One entry per settings kind the project registers, in the project asset's order.</summary>
    /// <param name="project">The open project.</param>
    /// <returns>The entries; empty for a project without settings assets.</returns>
    public static IReadOnlyList<ProjectSettingsEntry> Entries(IAppSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. ProjectSettingsLoader.FindSources(project.AssetsAbsoluteDir)
            .DistinctBy(static source => source.Kind)
            .Select(static source => new ProjectSettingsEntry(source.Kind, Label(source.Kind)))];
    }

    /// <summary>
    /// A cheap value that changes when the project asset is rewritten, for telling when <see cref="Entries"/> has
    /// to be read again.
    /// </summary>
    /// <param name="project">The open project.</param>
    /// <returns>The project asset's path and last write time.</returns>
    public static (string Path, DateTime Written) Stamp(IAppSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = ProjectAsset.PathIn(project.AssetsAbsoluteDir);
        return (path, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>The last segment of the kind's asset-creation path, or its type name split into words.</summary>
    /// <param name="kind">The settings type.</param>
    /// <returns>The label.</returns>
    public static string Label(Type kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (kind.GetCustomAttribute<CreateAssetMenuAttribute>()?.Path.Split('/').LastOrDefault() is
            { Length: > 0 } fromMenu)
        {
            return fromMenu;
        }

        return WordBoundary().Replace(kind.Name, " ");
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();
}
