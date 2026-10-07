

namespace Gaya.Host;

/// <summary>
/// The application shell. Owns nothing product-specific: it renders a menu bar, a dock space and a
/// status bar entirely from the contribution registries in a <see cref="GayaApplication"/>. Panels
/// are instantiated once via their factories and cached.
/// </summary>
public sealed partial class Workbench : IPanelAccessor, IDisposable
{
    static readonly string[] MenuOrder =
        [MenuIds.File, MenuIds.Edit, MenuIds.View, MenuIds.Project, MenuIds.Run, MenuIds.Help];

    static readonly Dictionary<string, string> MenuLabels = new()
    {
        [MenuIds.File] = "File",
        [MenuIds.Edit] = "Edit",
        [MenuIds.View] = "View",
        [MenuIds.Project] = "Project",
        [MenuIds.Run] = "Run",
        [MenuIds.Help] = "Help",
    };

    readonly GayaApplication app;
    readonly ILogger log;
    readonly WorkbenchLayoutStore layoutStore;
    readonly AppearanceBridge appearanceBridge;
    readonly Dictionary<string, IPanel> panelInstances = [];
    readonly Dictionary<string, IChromeItem> chromeInstances = [];
    readonly HashSet<string> knownPanels = [];
    readonly Dictionary<string, ParkedPanel> parkedPanels = [];
    Dictionary<string, PanelDescriptor> descriptors;

    int panelsRevision;
    int savedRevision;
    bool paletteOpen;
    DockTheme dockTheme;
    int styleSheetsVersion = -1;
    Gui? styledGui;

    /// <summary>Creates the workbench over an activated plugin set.</summary>
    /// <param name="app">The loaded plugins, services and registries.</param>
    /// <param name="theme">An extra theme sheet to offer and start with; the persisted one is used when null.</param>
    /// <param name="layoutStore">Where the dock layout is persisted; a default location is used when null.</param>
    public Workbench(GayaApplication app, ThemeSource? theme = null, WorkbenchLayoutStore? layoutStore = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        this.app = app;
        log = app.Services.GetService(typeof(ILogger)) as ILogger ?? NullLogger.Instance;
        Appearance = AppearanceSettings.Register(app.Settings);
        FrameRate = new FrameRateLimiter(FrameRateSettings.Register(app.Settings));

        if (theme is not null)
        {
            var info = app.Themes.Register(theme);
            app.Themes.ApplyColorTheme(info.Id);
            Appearance.Theme = info.Id;
        }

        appearanceBridge = new AppearanceBridge(Appearance, app.Themes, app.Settings);
        dockTheme = Theme.ToDockTheme();
        app.Themes.Changed += OnThemeChanged;
        this.layoutStore = layoutStore ?? new WorkbenchLayoutStore(log);
        descriptors = app.Panels.All.ToDictionary(descriptor => descriptor.Id);
        panelsRevision = app.Panels.Revision;

        Layout = RestoreOrSeedLayout();
        savedRevision = Layout.Revision;
    }

    /// <summary>The dock arrangement currently on screen.</summary>
    public DockLayout Layout { get; }

    /// <summary>The persisted preferences exposed by the Appearance settings page.</summary>
    public AppearanceSettings Appearance { get; }

    /// <summary>The desktop frame limiter driven by the performance settings page.</summary>
    public FrameRateLimiter FrameRate { get; }

    /// <summary>Whether the operating system supplies the title bar; changes are persisted through the settings API.</summary>
    public bool NativeTitlebar
    {
        get => Appearance.NativeTitlebar;
        set
        {
            if (Appearance.NativeTitlebar == value) return;
            Appearance.NativeTitlebar = value;
            app.Settings.NotifyChanged(AppearanceSettings.PageId);
        }
    }

    /// <summary>The theme every part of the workbench is drawn with, preview included.</summary>
    ThemeTokens Theme => app.Themes.Current;

    /// <summary>
    /// The shell language service a plugin contributes, or null when none does — the host has no
    /// concrete localization of its own, so its chrome stays in its authored language in that case.
    /// </summary>
    IShellLocalization? Shell => app.Services.GetService(typeof(IShellLocalization)) as IShellLocalization;

    /// <summary>Translates a piece of chrome text through the shell's language service, when there is one.</summary>
    string T(string text) => Shell?.T(text) ?? text;

    /// <summary>Rebuilds the metrics the dock space reads.</summary>
    void OnThemeChanged() => dockTheme = Theme.ToDockTheme();

    /// <summary>
    /// Installs the active theme's sheets into the GUI over Guinevere's default control sheet, so styled widgets
    /// resolve against them.
    /// </summary>
    void SyncStyleSheets(Gui gui)
    {
        if (styleSheetsVersion == app.Themes.SheetsVersion && styledGui == gui) return;

        styleSheetsVersion = app.Themes.SheetsVersion;
        styledGui = gui;
        gui.StyleSheets.Clear();
        foreach (var sheet in app.Themes.Sheets) gui.StyleSheets.Add(sheet);
        ExcaliburStyles.Ensure(gui);
    }

    /// <summary>
    /// Loads the saved layout and reconciles it against the panels actually registered — panels not
    /// registered yet are parked until they are, never-seen ones that open by default are folded in at
    /// their declared placement — or seeds a fresh layout when there is nothing usable on disk.
    /// </summary>
    DockLayout RestoreOrSeedLayout()
    {
        var restored = layoutStore.Load();
        if (restored is null) return SeedLayout();

        knownPanels.UnionWith(restored.KnownPanels);
        foreach (var (panelId, parked) in restored.ParkedPanels) parkedPanels[panelId] = parked;

        // A layout that knows no panels predates closed panels being remembered: it holds every panel
        // that was ever registered, wanted or not, so only those that open by default are kept.
        var layout = restored.Layout;
        var legacy = restored.KnownPanels.Count == 0;
        foreach (var panelId in layout.PanelIds.ToList())
        {
            if (descriptors.TryGetValue(panelId, out var descriptor))
            {
                if (legacy && !descriptor.OpenByDefault) layout.Remove(panelId);
            }
            else if (legacy) layout.Remove(panelId);
            else Park(layout, panelId);
        }

        foreach (var descriptor in app.Panels.All) Reconcile(layout, descriptor);

        log.LogDebug("Restored workbench layout from {Path}", layoutStore.Path);
        return layout;
    }

    /// <summary>
    /// Brings a registered panel into the layout if it belongs there: back to where it was parked, or at
    /// its declared placement the first time it is seen. A known panel that is absent was closed.
    /// </summary>
    void Reconcile(DockLayout layout, PanelDescriptor descriptor)
    {
        if (layout.Contains(descriptor.Id))
        {
            knownPanels.Add(descriptor.Id);
            return;
        }

        if (parkedPanels.ContainsKey(descriptor.Id) || (knownPanels.Add(descriptor.Id) && descriptor.OpenByDefault))
            Place(layout, descriptor);
    }

    /// <summary>
    /// Adds a panel where it was parked, falling back to its declared placement. Rejoining a tab group
    /// keeps that group's active tab.
    /// </summary>
    void Place(DockLayout layout, PanelDescriptor descriptor)
    {
        var panelId = descriptor.Id;
        knownPanels.Add(panelId);

        if (parkedPanels.Remove(panelId, out var parked))
        {
            if (parked.TabbedWith is { } sibling && layout.FindLeaf(sibling) is { } leaf)
            {
                var active = leaf.PanelIds.ElementAtOrDefault(leaf.ActiveIndex);
                layout.DockInto(panelId, leaf, DockZone.Center);
                if (active is not null) layout.Activate(active);
                return;
            }

            if (parked.FloatBounds is { } bounds)
            {
                layout.Float(panelId, bounds);
                return;
            }

            layout.EnsurePanel(panelId, parked.Zone, FractionFor(descriptor.DefaultPlacement));
            return;
        }

        if (descriptor.DefaultPlacement == PanelPlacement.Floating)
            layout.Float(panelId, new Rect(180, 140, WorkbenchLayoutStore.DefaultLeftWidth, WorkbenchLayoutStore.DefaultBottomHeight));
        else
            layout.EnsurePanel(panelId, ZoneFor(descriptor.DefaultPlacement), FractionFor(descriptor.DefaultPlacement));
    }

    /// <summary>Records where a panel sits and takes it out of the layout until it registers again.</summary>
    void Park(DockLayout layout, string panelId)
    {
        if (layout.FindLeaf(panelId) is not { } leaf) return;

        var window = layout.Floating.FirstOrDefault(candidate => candidate.Root.Leaves().Contains(leaf));
        parkedPanels[panelId] = new ParkedPanel(leaf.Zone,
            leaf.PanelIds.FirstOrDefault(id => id != panelId),
            window?.Bounds);
        layout.Remove(panelId);
    }

    /// <summary>
    /// Reconciles the panel set against whatever plugins currently register — a user assembly's own
    /// panels come and go with a recompile, the same way its commands and menu items already do. Cheap
    /// when nothing changed: a revision compare against <see cref="PanelRegistry"/>.
    /// </summary>
    void SyncPanels()
    {
        if (app.Panels.Revision == panelsRevision) return;

        panelsRevision = app.Panels.Revision;
        var current = app.Panels.All;
        ReleaseReplacedPanels(current);

        foreach (var goneId in descriptors.Keys.Except(current.Select(d => d.Id)).ToList())
        {
            if (panelInstances.Remove(goneId, out var panel) && panel is IDisposable disposable)
                disposable.Dispose();

            // A recompile unregisters a user panel and registers it again moments later.
            Park(Layout, goneId);
        }

        descriptors = current.ToDictionary(descriptor => descriptor.Id);

        foreach (var descriptor in current) Reconcile(Layout, descriptor);
    }

    void ReleaseReplacedPanels(IReadOnlyList<PanelDescriptor> current)
    {
        foreach (var descriptor in current)
        {
            if (!descriptors.TryGetValue(descriptor.Id, out var previous)
                || ReferenceEquals(previous, descriptor)) continue;
            if (panelInstances.Remove(descriptor.Id, out var panel) && panel is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <summary>Builds the initial arrangement from the declared placement of each panel that opens by default.</summary>
    DockLayout SeedLayout()
    {
        var layout = new DockLayout();
        knownPanels.UnionWith(app.Panels.All.Select(descriptor => descriptor.Id));
        var opening = app.Panels.All.Where(descriptor => descriptor.OpenByDefault).ToList();

        // Center first: the edges wrap whatever is already there, so seeding an edge into an empty
        // layout would make that panel the center.
        foreach (var descriptor in opening.Where(d => d.DefaultPlacement == PanelPlacement.Center))
            layout.DockAtEdge(descriptor.Id, DockZone.Center);

        foreach (var descriptor in opening.Where(d => d.DefaultPlacement != PanelPlacement.Center))
        {
            if (descriptor.DefaultPlacement == PanelPlacement.Floating)
            {
                layout.Float(descriptor.Id, new Rect(180, 140, WorkbenchLayoutStore.DefaultLeftWidth, WorkbenchLayoutStore.DefaultBottomHeight));
                continue;
            }

            layout.DockAtEdge(descriptor.Id, ZoneFor(descriptor.DefaultPlacement),
                FractionFor(descriptor.DefaultPlacement));
        }

        return layout;
    }

    static DockZone ZoneFor(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => DockZone.Left,
        PanelPlacement.Right => DockZone.Right,
        PanelPlacement.Bottom => DockZone.Bottom,
        _ => DockZone.Center
    };

    float FractionFor(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => WorkbenchLayoutStore.DefaultLeftWidth / 1600f,
        PanelPlacement.Right => WorkbenchLayoutStore.DefaultRightWidth / 1600f,
        PanelPlacement.Bottom => WorkbenchLayoutStore.DefaultBottomHeight / 950f,
        _ => 0.25f
    };

    /// <summary>Writes the layout out if the user has changed it since it was last saved.</summary>
    public void SaveLayoutIfChanged()
    {
        if (Layout.Revision == savedRevision) return;

        savedRevision = Layout.Revision;
        layoutStore.Save(new WorkbenchLayoutState(Layout, knownPanels, parkedPanels));
    }

    /// <summary>Persists the layout and the settings, and releases panels that own resources.</summary>
    public void Dispose()
    {
        SaveLayoutIfChanged();
        app.Settings.Save();
        appearanceBridge.Dispose();
        app.Themes.Changed -= OnThemeChanged;

        foreach (var panel in panelInstances.Values.OfType<IDisposable>()) panel.Dispose();
        // Plugins built outside this solution may contribute chrome items that own resources.
        // ReSharper disable SuspiciousTypeConversion.Global
        foreach (var item in chromeInstances.Values.OfType<IDisposable>()) item.Dispose();
        // ReSharper restore SuspiciousTypeConversion.Global
    }

    /// <summary>Runs a command by id (menus, buttons and the palette all funnel through here).</summary>
    public void ExecuteCommand(string commandId)
    {
        if (!app.Commands.TryGet(commandId, out var cmd))
        {
            log.LogWarning("Command {CommandId} is not registered", commandId);
            return;
        }

        if (cmd.CanExecute is { } guard && !guard(app.Services))
            return;

        try
        {
            cmd.Execute(app.Services);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Command {CommandId} threw", commandId);
        }
    }

    /// <summary>Whether a command is registered and its guard currently allows it.</summary>
    /// <param name="commandId">The command to test.</param>
    /// <returns>True when <see cref="ExecuteCommand"/> would run it.</returns>
    public bool CanExecuteCommand(string commandId) =>
        app.Commands.TryGet(commandId, out var cmd) && (cmd.CanExecute?.Invoke(app.Services) ?? true);

    /// <summary>The label a command shows right now, following its dynamic label.</summary>
    /// <param name="commandId">The command to label.</param>
    /// <returns>The label, or an empty string when the command is not registered.</returns>
    public string CommandLabel(string commandId) =>
        app.Commands.TryGet(commandId, out var cmd) ? cmd.MenuLabelFor(app.Services) : string.Empty;

    /// <summary>The per-frame draw callback; pass to <c>GuiWindow.RunGui</c>.</summary>
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);
        if (gui.Pass == Pass.Pass1Build)
        {
            SyncChrome();
            SyncWindowIdentity(gui);
        }
        SyncPanels();
        HandleShortcuts(gui);

        var t = Theme;
        SyncStyleSheets(gui);
        gui.DrawRect(gui.ScreenRect, t.Background);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(t.Gap).Padding(t.Gap).Enter())
        {
            MenuBar(gui);
            ChromeStrip(gui, ChromeSlot.Toolbar);
            ChromeStrip(gui, ChromeSlot.TopBar);

            using (gui.Node().Expand().ExpandWidth().Direction(Axis.Horizontal).Gap(t.Gap).Enter())
            {
                if (paletteOpen) CommandPalette(gui);
                else gui.DockSpace(Layout, PanelInfo, RenderPanel, dockTheme, RenderTabStripActions);
            }

            StatusBar(gui);
        }

        RenderOverlays(gui);

        RenderBlocker(gui);

        if (gui.Pass != Pass.Pass2Render) return;

        SaveLayoutIfChanged();
        app.Settings.Flush();

        // A theme preview lasts one frame: whatever renewed it above has just drawn, so anything that
        // did not is dropped here and the committed theme comes back.
        app.Themes.EndFrame();
        TickPlugins(gui.Time.DeltaTime);
    }
}
