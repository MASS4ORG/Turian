namespace Gaya.Host;

/// <summary>
/// Panel contributions. Registering and removing at runtime is what lets user code contribute a panel
/// that comes and goes with a recompile, the same way commands and menu items already do.
/// </summary>
public sealed class PanelRegistry : IPanelRegistry
{
    readonly List<PanelDescriptor> panels = [];

    /// <summary>Bumped on every add or remove, so the workbench can cheaply tell it needs to resync.</summary>
    public int Revision { get; private set; }

    /// <inheritdoc />
    public void Register(PanelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var existing = panels.FindIndex(p => p.Id == descriptor.Id);
        if (existing >= 0) panels[existing] = descriptor;
        else panels.Add(descriptor);

        Revision++;
    }

    /// <inheritdoc />
    public void Remove(string panelId)
    {
        if (panels.RemoveAll(p => p.Id == panelId) > 0) Revision++;
    }

    /// <summary>All registered panels, in registration order.</summary>
    public IReadOnlyList<PanelDescriptor> All => panels;

    /// <summary>Panels whose default placement is <paramref name="placement"/>.</summary>
    public IEnumerable<PanelDescriptor> For(PanelPlacement placement) =>
        panels.Where(p => p.DefaultPlacement == placement);
}

/// <summary>Command contributions keyed by id; a later registration of the same id wins.</summary>
public sealed class CommandRegistry : ICommandRegistry, ICommandCatalog
{
    readonly Dictionary<string, CommandDescriptor> commands = [];

    /// <inheritdoc />
    public void Register(CommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        commands[descriptor.Id] = descriptor;
    }

    /// <inheritdoc />
    public void Remove(string commandId) => commands.Remove(commandId);

    /// <summary>All commands, ordered by title.</summary>
    public IReadOnlyList<CommandDescriptor> All => commands.Values.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Looks a command up by id.</summary>
    public bool TryGet(string id, out CommandDescriptor descriptor) => commands.TryGetValue(id, out descriptor!);

    /// <inheritdoc />
    IReadOnlyList<CommandDescriptor> ICommandCatalog.Commands => All;

    /// <inheritdoc />
    public CommandDescriptor? Find(string commandId) => commands.GetValueOrDefault(commandId);
}

/// <summary>Menu-bar item contributions.</summary>
public sealed class MenuRegistry : IMenuRegistry
{
    readonly List<MenuItemDescriptor> items = [];
    readonly Dictionary<string, (string Label, int Order)> declared = [];

    /// <inheritdoc />
    public void Add(MenuItemDescriptor item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Replacing in place keeps the contributor's ordering when it re-registers, which is what a
        // recompile does to every user-code item at once.
        var existing = items.FindIndex(i => i.MenuId == item.MenuId && i.CommandId == item.CommandId);
        if (existing >= 0) items[existing] = item;
        else items.Add(item);
    }

    /// <inheritdoc />
    public void Remove(string commandId) => items.RemoveAll(i => i.CommandId == commandId);

    /// <inheritdoc />
    public void DeclareMenu(string menuId, string label, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuId);
        declared[menuId] = (label, order);
    }

    /// <summary>Top-level menus declared beyond the well-known ones, in their declared order.</summary>
    public IEnumerable<(string MenuId, string Label)> DeclaredMenus =>
        declared.OrderBy(entry => entry.Value.Order)
                .ThenBy(entry => entry.Value.Label, StringComparer.OrdinalIgnoreCase)
                .Select(entry => (entry.Key, entry.Value.Label));

    /// <summary>Distinct menu ids that have at least one item, in first-seen order.</summary>
    public IReadOnlyList<string> MenuIdsInUse => items.Select(i => i.MenuId).Distinct().ToList();

    /// <summary>Items under <paramref name="menuId"/>, sorted by group then order.</summary>
    public IEnumerable<MenuItemDescriptor> ItemsFor(string menuId) =>
        items.Where(i => i.MenuId == menuId)
              .OrderBy(i => i.Group, StringComparer.Ordinal)
              .ThenBy(i => i.Order);
}

/// <summary>In-memory <see cref="IChromeRegistry"/>.</summary>
public sealed class ChromeRegistry : IChromeRegistry
{
    readonly List<ChromeDescriptor> chrome = [];

    /// <inheritdoc />
    public void Register(ChromeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (chrome.Any(item => item.Id == descriptor.Id))
            throw new InvalidOperationException($"Duplicate chrome id '{descriptor.Id}'.");
        chrome.Add(descriptor);
    }

    /// <summary>All registered chrome, in registration order.</summary>
    public IReadOnlyList<ChromeDescriptor> All => chrome;

    /// <summary>Chrome for a slot, in <see cref="ChromeDescriptor.Order"/> then registration order.</summary>
    public IEnumerable<ChromeDescriptor> For(ChromeSlot slot) =>
        chrome.Where(item => item.Slot == slot).OrderBy(item => item.Order);
}

/// <summary>In-memory <see cref="ITabStripChromeRegistry"/>.</summary>
public sealed class TabStripChromeRegistry : ITabStripChromeRegistry
{
    readonly List<TabStripChromeDescriptor> chrome = [];

    /// <inheritdoc />
    public void Register(TabStripChromeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (chrome.Any(item => item.Id == descriptor.Id))
            throw new InvalidOperationException($"Duplicate tab strip chrome id '{descriptor.Id}'.");
        chrome.Add(descriptor);
    }

    /// <summary>All registered tab strip chrome, in registration order.</summary>
    public IReadOnlyList<TabStripChromeDescriptor> All => chrome;

    /// <summary>Tab strip chrome for one panel, in registration order.</summary>
    public IEnumerable<TabStripChromeDescriptor> For(string panelId) =>
        chrome.Where(item => item.PanelId == panelId);
}

/// <summary>
/// Which panel the workbench considers focused. The workbench sets it as the user clicks into a
/// panel; panel-scoped shortcuts are matched against it.
/// </summary>
public sealed class FocusTracker : IFocusTracker
{
    /// <inheritdoc />
    public string ActivePanelId { get; private set; } = ShortcutContexts.Global;

    /// <inheritdoc />
    public event Action<string>? Changed;

    /// <inheritdoc />
    public void Focus(string panelId)
    {
        var next = panelId;
        if (next == ActivePanelId) return;

        ActivePanelId = next;
        Changed?.Invoke(next);
    }
}

/// <summary>
/// The <see cref="IShellHost"/> the host registers before plugins are configured, so a command can
/// ask to exit before the window that answers the request exists.
/// </summary>
public sealed class ShellHost : IShellHost
{
    /// <summary>Raised when a command asked the application to close.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when a command asked for a panel to be shown, with the panel's id.</summary>
    public event Action<string>? PanelRequested;

    /// <summary>Raised when a command asked for the command palette to be shown or hidden.</summary>
    public event Action? CommandPaletteRequested;

    /// <inheritdoc />
    public void RequestExit() => ExitRequested?.Invoke();

    /// <inheritdoc />
    public void ShowPanel(string panelId) => PanelRequested?.Invoke(panelId);

    /// <inheritdoc />
    public void ToggleCommandPalette() => CommandPaletteRequested?.Invoke();
}

/// <summary>
/// The <see cref="IPanelAccessor"/> the host registers before plugins are configured, then points at
/// the workbench that owns the panel instances. Until then it reaches no panel, which is what a
/// focus-scoped command arriving during startup should find.
/// </summary>
public sealed class PanelAccessor : IPanelAccessor
{
    Workbench? workbench;

    /// <summary>Points the accessor at the workbench that instantiates panels.</summary>
    /// <param name="target">The workbench holding the panel instances.</param>
    public void Bind(Workbench target) => workbench = target;

    /// <inheritdoc />
    public IPanel? Panel(string panelId) => workbench?.Panel(panelId);

    /// <inheritdoc />
    public string Title(string panelId) => workbench?.Title(panelId) ?? panelId;
}

/// <summary>
/// The <see cref="ICommandDispatcher"/> the host registers before plugins are configured, then points
/// at the workbench once it exists. Until then every call is a no-op, which is what a command arriving
/// during startup should do.
/// </summary>
public sealed class CommandDispatcher : ICommandDispatcher
{
    Workbench? workbench;

    /// <summary>Points the dispatcher at the workbench that owns the registries.</summary>
    /// <param name="target">The workbench commands are run against.</param>
    public void Bind(Workbench target) => workbench = target;

    /// <inheritdoc />
    public void Execute(string commandId) => workbench?.ExecuteCommand(commandId);

    /// <inheritdoc />
    public bool CanExecute(string commandId) => workbench?.CanExecuteCommand(commandId) ?? false;

    /// <inheritdoc />
    public string Label(string commandId) => workbench?.CommandLabel(commandId) ?? string.Empty;
}
