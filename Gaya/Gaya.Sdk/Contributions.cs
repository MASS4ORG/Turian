namespace Gaya.Sdk;

/// <summary>Where the workbench docks a panel by default. The user can move it later.</summary>
public enum PanelPlacement
{
    /// <summary>Left dock column.</summary>
    Left,

    /// <summary>Right dock column.</summary>
    Right,

    /// <summary>Bottom dock row.</summary>
    Bottom,

    /// <summary>The central editor area.</summary>
    Center,

    /// <summary>A free-floating window.</summary>
    Floating,
}

/// <summary>
/// A panel view. Pure rendering: identity and title live in the <see cref="PanelDescriptor"/>.
/// The workbench calls <see cref="Render"/> every frame inside a layout node sized to the
/// panel's docked region.
/// </summary>
public interface IPanel
{
    /// <summary>Draws the panel body for this frame.</summary>
    /// <param name="gui">The Guinevere context.</param>
    void Render(Gui gui);
}

/// <summary>A dockable panel contribution.</summary>
/// <param name="Id">Stable unique id, e.g. <c>gaya.example.sceneTree</c>.</param>
/// <param name="Title">Title shown in the panel header / tab.</param>
/// <param name="DefaultPlacement">Where the workbench docks it when there is no saved layout.</param>
/// <param name="Factory">Creates the panel instance, resolving its dependencies from the provider.</param>
public sealed record PanelDescriptor(
    string Id,
    string Title,
    PanelPlacement DefaultPlacement,
    Func<IServiceProvider, IPanel> Factory)
{
    /// <summary>
    /// Whether the workbench opens the panel the first time it sees it. Panels reached through a
    /// command — settings pages, tools, a user's own windows — set this to false and appear only once
    /// asked for; after that the saved layout decides.
    /// </summary>
    public bool OpenByDefault { get; init; } = true;
}

/// <summary>Collects <see cref="PanelDescriptor"/> contributions.</summary>
public interface IPanelRegistry
{
    /// <summary>Adds a panel contribution. Re-adding the same id replaces the earlier one.</summary>
    void Register(PanelDescriptor descriptor);

    /// <summary>Removes a panel, for a contributor whose set changes at runtime — a recompile, say.</summary>
    /// <param name="panelId">The panel to remove.</param>
    void Remove(string panelId);
}

/// <summary>An invokable command contribution.</summary>
/// <param name="Id">Stable unique id, e.g. <c>gaya.file.openProject</c>.</param>
/// <param name="Title">Label shown in menus and the command palette.</param>
/// <param name="Execute">Runs the command, resolving what it needs from the provider.</param>
/// <param name="CanExecute">Optional guard; when it returns false the command is shown disabled.</param>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    Action<IServiceProvider> Execute,
    Func<IServiceProvider, bool>? CanExecute = null)
{
    /// <summary>
    /// Short label a menu shows, where the surrounding menu already says which area the command
    /// belongs to. Empty falls back to <see cref="Title"/>, which is qualified for the palette.
    /// </summary>
    public string MenuLabel { get; init; } = "";

    /// <summary>
    /// Resolves the menu label at draw time, for a command whose name follows editor state — Play
    /// becomes Stop while a session runs. Wins over <see cref="MenuLabel"/> when set.
    /// </summary>
    public Func<IServiceProvider, string>? DynamicLabel { get; init; }

    /// <summary>The label a menu should draw right now.</summary>
    /// <param name="services">The provider the label is resolved against.</param>
    /// <returns>The dynamic label, else <see cref="MenuLabel"/>, else <see cref="Title"/>.</returns>
    public string MenuLabelFor(IServiceProvider services) =>
        DynamicLabel?.Invoke(services) ?? (MenuLabel.Length > 0 ? MenuLabel : Title);
}

/// <summary>Collects <see cref="CommandDescriptor"/> contributions.</summary>
public interface ICommandRegistry
{
    /// <summary>Adds a command contribution. A duplicate id replaces the earlier one.</summary>
    void Register(CommandDescriptor descriptor);

    /// <summary>Removes a command, for a contributor whose set changes at runtime.</summary>
    /// <param name="commandId">The command to remove.</param>
    void Remove(string commandId);
}

/// <summary>Well-known menu-bar ids. Plugins may also invent their own submenu ids.</summary>
public static class MenuIds
{
    /// <summary>The <c>File</c> menu.</summary>
    public const string File = "menubar/file";

    /// <summary>The <c>Edit</c> menu.</summary>
    public const string Edit = "menubar/edit";

    /// <summary>The <c>View</c> menu.</summary>
    public const string View = "menubar/view";

    /// <summary>The <c>Run</c> menu.</summary>
    public const string Run = "menubar/run";

    /// <summary>The <c>Help</c> menu.</summary>
    public const string Help = "menubar/help";

    /// <summary>The <c>Project</c> menu.</summary>
    public const string Project = "menubar/project";
}

/// <summary>A menu item that invokes a command.</summary>
/// <param name="MenuId">Target menu, e.g. <see cref="MenuIds.View"/>.</param>
/// <param name="CommandId">Command to invoke; its title is the item label.</param>
/// <param name="Group">Optional group name; items sort by group then <paramref name="Order"/>.</param>
/// <param name="Order">Sort order within the group.</param>
/// <param name="Path">
/// Optional <c>/</c>-separated submenu path inside the menu, e.g. <c>Level</c> for an item that
/// should sit under a <c>Level</c> submenu. Empty puts the item at the menu's top level.
/// </param>
public sealed record MenuItemDescriptor(
    string MenuId,
    string CommandId,
    string Group = "",
    int Order = 0,
    string Path = "");

/// <summary>Collects <see cref="MenuItemDescriptor"/> contributions.</summary>
public interface IMenuRegistry
{
    /// <summary>Adds a menu item. Re-adding the same command to the same menu replaces the earlier entry.</summary>
    void Add(MenuItemDescriptor item);

    /// <summary>Drops every item that invokes a command, so it can be re-contributed.</summary>
    /// <param name="commandId">The command whose items are removed.</param>
    void Remove(string commandId);

    /// <summary>
    /// Names a top-level menu beyond the well-known <see cref="MenuIds"/>, so the workbench knows what
    /// to label it and where to put it. Declaring the same id twice updates the label.
    /// </summary>
    /// <param name="menuId">The menu id items target.</param>
    /// <param name="label">The title drawn in the menu bar.</param>
    /// <param name="order">Sort order among declared menus; they follow the well-known ones.</param>
    void DeclareMenu(string menuId, string label, int order = 0);
}

/// <summary>
/// Runs registered commands by id, with the same guard and error handling the menus apply. Panels and
/// chrome resolve this instead of re-deriving what a command does, so a toolbar button and its menu
/// entry can never drift apart.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>Runs a command, if it is registered and its guard allows it.</summary>
    /// <param name="commandId">The command to run.</param>
    void Execute(string commandId);

    /// <summary>Whether a command is registered and currently allowed to run.</summary>
    /// <param name="commandId">The command to test.</param>
    /// <returns>True when it would run.</returns>
    bool CanExecute(string commandId);

    /// <summary>The label the command would show right now, following its dynamic label.</summary>
    /// <param name="commandId">The command to label.</param>
    /// <returns>The label, or an empty string when the command is not registered.</returns>
    string Label(string commandId);
}

/// <summary>
/// Host operations a command may need that no service can provide, because they act on the window
/// rather than on the project.
/// </summary>
public interface IShellHost
{
    /// <summary>Asks the host to shut down: plugins stop, the layout is saved, the window closes.</summary>
    void RequestExit();

    /// <summary>
    /// Brings a panel to the front, opening it at its declared placement when it was closed. A command
    /// that exists to show a panel — File / Settings — calls this rather than touching the layout.
    /// </summary>
    /// <param name="panelId">The panel to show.</param>
    void ShowPanel(string panelId);

    /// <summary>Opens the command palette, or closes it when it is already open.</summary>
    void ToggleCommandPalette();
}

/// <summary>Where a chrome contribution is drawn: fixed strips the dock space never covers.</summary>
public enum ChromeSlot
{
    /// <summary>
    /// The menu bar's own row, filling what the menus leave over — compact window-wide controls such
    /// as the play buttons, which cost no vertical space of their own there.
    /// </summary>
    MenuBar,

    /// <summary>
    /// The strip directly under the menu bar, above everything else — window-wide actions too tall to
    /// share the menu row. Drawn only when something is registered for it.
    /// </summary>
    Toolbar,

    /// <summary>A strip under the toolbar, spanning the window — document tabs.</summary>
    TopBar,

    /// <summary>A segment of the status bar along the bottom.</summary>
    StatusBar
}

/// <summary>Chrome drawn outside the dock space, such as a document tab strip or a play toolbar.</summary>
public interface IChromeItem
{
    /// <summary>Draws the item into the strip it was registered for.</summary>
    /// <param name="gui">The GUI for this frame.</param>
    void Render(Gui gui);
}

/// <summary>Reports whether the host must block all editor input for an important operation.</summary>
public interface IUiBlocker
{
    /// <summary>Whether a modal input-blocking overlay should be shown.</summary>
    bool IsBlocked { get; }

    /// <summary>Human-readable operation shown on the overlay.</summary>
    string Message { get; }
}

/// <summary>A chrome contribution.</summary>
/// <param name="Id">Stable unique id.</param>
/// <param name="Slot">Which strip it belongs to.</param>
/// <param name="Factory">Builds the item; called once and cached.</param>
/// <param name="Height">Strip height in pixels. Zero lets the workbench choose.</param>
/// <param name="Order">Lower sorts first within the slot.</param>
public sealed record ChromeDescriptor(
    string Id,
    ChromeSlot Slot,
    Func<IServiceProvider, IChromeItem> Factory,
    float Height = 0,
    int Order = 0);

/// <summary>Registry of chrome contributions.</summary>
public interface IChromeRegistry
{
    /// <summary>Adds a chrome contribution.</summary>
    /// <param name="descriptor">The contribution.</param>
    void Register(ChromeDescriptor descriptor);
}

/// <summary>
/// A chrome contribution anchored to the tab strip of one specific panel, drawn in the width the
/// panel group's tabs leave over — where an overflow or lock button belongs.
/// </summary>
/// <param name="Id">Stable unique id.</param>
/// <param name="PanelId">The panel whose tab strip hosts the item; anything else leaves it hidden.</param>
/// <param name="Factory">Builds the item; called once and cached.</param>
public sealed record TabStripChromeDescriptor(
    string Id,
    string PanelId,
    Func<IServiceProvider, IChromeItem> Factory);

/// <summary>Registry of <see cref="TabStripChromeDescriptor"/> contributions.</summary>
public interface ITabStripChromeRegistry
{
    /// <summary>Adds a chrome contribution to a panel's tab strip.</summary>
    /// <param name="descriptor">The contribution.</param>
    void Register(TabStripChromeDescriptor descriptor);
}
