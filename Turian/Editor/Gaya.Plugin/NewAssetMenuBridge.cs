namespace Gaya.Plugin.Turian;

/// <summary>
/// Publishes one File / New entry per asset the Create menu offers, and republishes when editor bricks or scripts
/// change what can be created. Choosing one writes the asset into the project's Assets folder and opens it.
/// </summary>
sealed class NewAssetMenuBridge : IDisposable
{
    const string submenu = "New";

    // Sorts the submenu above the other entries of the File menu's first group.
    const int firstOrder = -1000;

    readonly AssetCreationCatalog catalog;
    readonly EditorAssemblySet assemblies;
    readonly ICommandRegistry commands;
    readonly IMenuRegistry menus;

    readonly List<string> published = [];
    IReadOnlyList<Assembly>? publishedFor;
    volatile bool stale = true;

    /// <summary>Creates the bridge; nothing is published before the first <see cref="Sync"/>.</summary>
    /// <param name="catalog">What can be created.</param>
    /// <param name="assemblies">Tells when editor assemblies join or leave, which changes what can be created.</param>
    /// <param name="commands">Where the entries are registered as commands.</param>
    /// <param name="menus">Where the entries are registered as menu items.</param>
    public NewAssetMenuBridge(AssetCreationCatalog catalog, EditorAssemblySet assemblies, ICommandRegistry commands,
        IMenuRegistry menus)
    {
        this.catalog = catalog;
        this.assemblies = assemblies;
        this.commands = commands;
        this.menus = menus;

        catalog.Changed += Invalidate;
    }

    /// <summary>Republishes when what can be created may have changed; called once a frame.</summary>
    public void Sync()
    {
        var current = assemblies.Current;
        if (!stale && ReferenceEquals(current, publishedFor))
        {
            return;
        }

        publishedFor = current;
        stale = false;
        Publish([.. catalog.Kinds]);
    }

    void Invalidate() => stale = true;

    // Folders are made where they are wanted, in the asset browser.
    void Publish(IReadOnlyList<AssetCreationKind> kinds)
    {
        Unpublish();

        foreach (var kind in kinds.Where(static kind => kind.MenuPath != "Folder"))
        {
            var created = kind;
            string[] segments = [.. kind.MenuPath.Split('/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            var id = $"gaya.turian.new/{kind.MenuPath}/{kind.PayloadType?.FullName}";
            commands.Register(new CommandDescriptor(id, $"File: New {segments[^1]}",
                sp => Create(sp, created),
                static sp => sp.GetRequiredService<SettingsService>().Settings is not null)
            { MenuLabel = segments[^1] });
            menus.Add(new MenuItemDescriptor(MenuIds.File, id, "1", firstOrder + published.Count,
                string.Join('/', [submenu, .. segments[..^1]])));
            published.Add(id);
        }
    }

    static void Create(IServiceProvider services, AssetCreationKind kind)
    {
        if (services.GetRequiredService<SettingsService>().Settings is { } project
            && services.GetRequiredService<AssetCreationCatalog>().CreateAt(kind, project.AssetsAbsoluteDir)
                is { } path)
        {
            services.GetRequiredService<AssetOpenService>().OpenCreated(path);
        }
    }

    void Unpublish()
    {
        foreach (var id in published)
        {
            menus.Remove(id);
            commands.Remove(id);
        }

        published.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        catalog.Changed -= Invalidate;
        Unpublish();
    }
}
