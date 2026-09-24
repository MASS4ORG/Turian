namespace Gaya.Plugin.Turian;

/// <summary>
/// Publishes the menu entries user code contributes with <c>[MenuItem]</c> into the workbench menu
/// bar, and re-publishes them whenever a recompile swaps the user assembly. The discovery itself is
/// framework-agnostic and lives in <see cref="UserMenuCatalog"/>; this only maps paths onto Gaya
/// contributions.
/// </summary>
sealed class UserMenuBridge : IDisposable
{
    /// <summary>Sorts user items after every built-in group, so they gather below a separator.</summary>
    const string userGroup = "9";

    readonly UserMenuCatalog catalog;
    readonly ICommandRegistry commands;
    readonly IMenuRegistry menus;
    readonly ILogger log;

    readonly List<string> published = [];

    /// <summary>Creates the bridge and follows the catalog.</summary>
    /// <param name="catalog">Supplies the discovered entries.</param>
    /// <param name="commands">Where the entries are registered as commands.</param>
    /// <param name="menus">Where the entries are registered as menu items.</param>
    /// <param name="log">Where a republish is reported.</param>
    public UserMenuBridge(UserMenuCatalog catalog, ICommandRegistry commands, IMenuRegistry menus, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        this.catalog = catalog;
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
        foreach (var commandId in published)
        {
            menus.Remove(commandId);
            commands.Remove(commandId);
        }

        published.Clear();

        foreach (var entry in catalog.Commands)
        {
            var invoke = entry.Invoke;

            commands.Register(new CommandDescriptor(entry.Id, $"{entry.Menu}: {entry.Label}", invoke)
            {
                MenuLabel = entry.Label,
            });

            var menuId = MenuIdFor(entry.Menu);
            menus.DeclareMenu(menuId, entry.Menu);
            menus.Add(new MenuItemDescriptor(menuId, entry.Id, userGroup, published.Count, entry.SubmenuPath));
            published.Add(entry.Id);
        }

        log.LogDebug("User menu: published {Count} item(s)", published.Count);
    }

    /// <summary>
    /// The well-known menu names, so <c>[MenuItem("File/…")]</c> lands in the studio's own File menu
    /// rather than opening a second one beside it.
    /// </summary>
    static readonly Dictionary<string, string> wellKnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["File"] = MenuIds.File,
        ["Edit"] = MenuIds.Edit,
        ["View"] = MenuIds.View,
        ["Run"] = MenuIds.Run,
        ["Help"] = MenuIds.Help,
    };

    /// <summary>A path's first segment names its top-level menu.</summary>
    static string MenuIdFor(string menu) =>
        wellKnownMenus.TryGetValue(menu, out var id) ? id : $"menubar/{menu}";

    /// <inheritdoc />
    public void Dispose() => catalog.Changed -= Publish;
}
