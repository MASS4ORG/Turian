namespace Gaya.Plugin.Turian;

/// <summary>
/// Publishes the panels user code contributes with <c>[Panel]</c> into the workbench's dock and menu
/// bar, and re-publishes them whenever a recompile swaps the user assembly. The discovery itself is
/// framework-agnostic and lives in <see cref="UserPanelCatalog"/>; this only maps pages onto Gaya
/// contributions.
/// </summary>
sealed class UserPanelBridge : IDisposable
{
    /// <summary>Sorts user panel items after every built-in group, so they gather below a separator.</summary>
    const string userGroup = "9";

    readonly UserPanelCatalog catalog;
    readonly IPanelRegistry panels;
    readonly ICommandRegistry commands;
    readonly IMenuRegistry menus;
    readonly ILogger log;

    readonly List<string> published = [];

    /// <summary>Creates the bridge and follows the catalog.</summary>
    /// <param name="catalog">Supplies the discovered panels.</param>
    /// <param name="panels">Where a panel is registered.</param>
    /// <param name="commands">Where the panel's "show" command is registered.</param>
    /// <param name="menus">Where the panel's menu item is registered.</param>
    /// <param name="log">Where a republish is reported.</param>
    public UserPanelBridge(UserPanelCatalog catalog, IPanelRegistry panels, ICommandRegistry commands,
        IMenuRegistry menus, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        this.catalog = catalog;
        this.panels = panels;
        this.commands = commands;
        this.menus = menus;
        this.log = log;

        catalog.Changed += Publish;
    }

    /// <summary>
    /// Rescans if the user assembly was swapped. Called once a frame; it costs a reference comparison
    /// when nothing changed.
    /// </summary>
    public void Sync() => catalog.EnsureRefreshed();

    void Publish()
    {
        foreach (var id in published)
        {
            menus.Remove(id);
            commands.Remove(id);
            panels.Remove(id);
        }

        published.Clear();

        foreach (var page in catalog.Pages)
        {
            panels.Register(new PanelDescriptor(page.Id, page.Name, PanelPlacement.Floating,
                _ => new UserPanel(page))
            {
                OpenByDefault = false,
            });

            commands.Register(new CommandDescriptor(page.Id, $"Panel: {page.Name}",
                sp => sp.GetRequiredService<IShellHost>().ShowPanel(page.Id))
            {
                MenuLabel = page.Name,
            });

            var menu = TopMenuOf(page.Path);
            var menuId = MenuIdFor(menu);
            menus.DeclareMenu(menuId, menu);
            menus.Add(new MenuItemDescriptor(menuId, page.Id, userGroup, published.Count, SubmenuPathOf(page.Path)));
            published.Add(page.Id);
        }

        log.LogDebug("User panels: published {Count} page(s)", published.Count);
    }

    /// <summary>
    /// The well-known menu names, so <c>[Panel(..., "View/…")]</c> lands in the studio's own View menu
    /// rather than opening a second one beside it.
    /// </summary>
    static readonly Dictionary<string, string> wellKnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["File"] = MenuIds.File,
        ["Edit"] = MenuIds.Edit,
        ["View"] = MenuIds.View,
        ["Project"] = MenuIds.Project,
        ["Run"] = MenuIds.Run,
        ["Help"] = MenuIds.Help,
    };

    /// <summary>A path's first segment — which top-level menu the panel's item belongs under.</summary>
    static string TopMenuOf(string path) => path.Split('/', 2)[0];

    /// <summary>
    /// The segments between the menu and the item, as a <c>/</c>-joined path, or an empty string when
    /// the item sits directly in its top-level menu.
    /// </summary>
    static string SubmenuPathOf(string path)
    {
        var segments = path.Split('/');
        return segments.Length <= 2 ? string.Empty : string.Join('/', segments[1..^1]);
    }

    static string MenuIdFor(string menu) =>
        wellKnownMenus.TryGetValue(menu, out var id) ? id : $"menubar/{menu}";

    /// <inheritdoc />
    public void Dispose() => catalog.Changed -= Publish;
}
