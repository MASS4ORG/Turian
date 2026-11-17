namespace Gaya.Plugin.Turian;

/// <summary>
/// Publishes one Project / Project Settings entry per settings asset the open project registers, and republishes when
/// the project is opened, closed or its project asset changes. The kinds come from
/// <see cref="ProjectSettingsMenu"/>; this only maps them onto Gaya contributions.
/// </summary>
sealed class ProjectSettingsMenuBridge(
    SettingsService project,
    ICommandRegistry commands,
    IMenuRegistry menus)
{
    const string submenu = "Project Settings";

    readonly List<string> published = [];
    (string Path, DateTime Written)? stamp;
    string? publishedFor;

    /// <summary>Republishes when the open project or its project asset changed; called once a frame.</summary>
    public void Sync()
    {
        var current = project.Settings;
        var directory = current?.ProjectAbsoluteDir;
        var next = current is null ? ((string, DateTime)?)null : ProjectSettingsMenu.Stamp(current);
        if (directory == publishedFor && Equals(next, stamp))
        {
            return;
        }

        publishedFor = directory;
        stamp = next;
        Publish(current);
    }

    void Publish(IAppSettings? current)
    {
        foreach (var commandId in published)
        {
            menus.Remove(commandId);
            commands.Remove(commandId);
        }

        published.Clear();
        if (current is null)
        {
            return;
        }

        foreach (var (entry, index) in ProjectSettingsMenu.Entries(current).Select(static (entry, index) => (entry, index)))
        {
            var kind = entry.Kind;
            commands.Register(new CommandDescriptor(entry.CommandId, $"Settings: {entry.Label}",
                sp => StudioCommands.OpenProjectSettings(sp, kind))
            {
                DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T(entry.Label),
            });
            menus.Add(new MenuItemDescriptor(MenuIds.Project, entry.CommandId, "1-settings", 2 + index, submenu));
            published.Add(entry.CommandId);
        }
    }
}
