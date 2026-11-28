

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
    readonly Dictionary<string, IChromeItem> chromeInstances = [];
    Dictionary<string, PanelDescriptor> descriptors;

    int panelsRevision;
    bool paletteOpen;
    int styleSheetsVersion = -1;
    Gui? styledGui;
    ThemeTokens? styledTheme;

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
        this.layoutStore = layoutStore ?? new WorkbenchLayoutStore(log);
        descriptors = app.Panels.All.ToDictionary(static descriptor => descriptor.Id);
        panelsRevision = app.Panels.Revision;

        savedLayouts = this.layoutStore.Load();
        activeSlot = SlotFor(app.Documents.Active?.Kind);
        frameSlot = activeSlot;
        foreach (var kind in app.DocumentKinds.All)
        {
            SlotFor(kind.Id);
        }

        RegisterLayoutCommands();
        SubscribeDocuments();
    }

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
            if (Appearance.NativeTitlebar == value)
            {
                return;
            }

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

    /// <summary>
    /// Installs the active theme's sheets into the GUI over the look's base sheet (Guinevere's default control sheet
    /// for the default look), so styled widgets resolve against them.
    /// </summary>
    void SyncStyleSheets(Gui gui)
    {
        var theme = Theme;
        if (styleSheetsVersion == app.Themes.SheetsVersion && styledGui == gui && styledTheme == theme)
        {
            return;
        }

        styleSheetsVersion = app.Themes.SheetsVersion;
        styledGui = gui;
        styledTheme = theme;
        if (!gui.IconDecoders.Contains(SvgIconDecoder.Instance))
        {
            gui.IconDecoders.Add(SvgIconDecoder.Instance);
        }

        gui.StyleSheets.Clear();
        foreach (var sheet in app.Themes.Sheets)
        {
            gui.StyleSheets.Add(sheet);
        }
        gui.StyleSheets.Add(StyleSheet.Parse(FormattableString.Invariant($$"""
            appbar { height = {{Math.Max(24f, theme.Scale(theme.MenuHeight))}}; }
            tabstrip { height = {{theme.Scale(theme.HeaderHeight)}}; font-size = {{theme.Text(theme.FontSize)}}; }
            dock-icon { width = {{theme.Scale(12)}}; }
            splitter.dock.horizontal { width = {{theme.Gap}}; }
            splitter.dock.vertical { height = {{theme.Gap}}; }
            treeview { font-size = {{theme.Text(theme.FontSize)}}; }
            tree-row { height = {{theme.Scale(theme.RowHeight)}}; indent = {{theme.Scale(14)}}; }
            tree-expander { width = {{theme.Scale(10)}}; }
            tree-icon { width = {{theme.Scale(12)}}; }
            """)));

        ExcaliburStyles.SetBaseSheet(gui, app.Themes.Look);
    }

    /// <summary>Persists the layout and the settings, and releases panels that own resources.</summary>
    public void Dispose()
    {
        app.Documents.ActivationSwitched -= OnActivationSwitched;
        app.Documents.ClosingDocument -= OnDocumentClosing;
        SaveLayoutIfChanged();
        app.Settings.Save();
        appearanceBridge.Dispose();

        foreach (var panel in panelInstances.Values.OfType<IDisposable>())
        {
            panel.Dispose();
        }
        // Plugins built outside this solution may contribute chrome items that own resources.
        // ReSharper disable SuspiciousTypeConversion.Global
        foreach (var item in chromeInstances.Values.OfType<IDisposable>())
        {
            item.Dispose();
        }
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
        {
            return;
        }

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

        // Both passes draw the same document and layout, even when a click in the second one switches them.
        if (gui.Pass == Pass.Pass1Build)
        {
            frameDocument = app.Documents.Active;
            frameSlot = activeSlot;
        }

        rendering = true;
        try
        {
            Draw(gui);
        }
        finally
        {
            rendering = false;
        }

        if (gui.Pass == Pass.Pass2Render)
        {
            RunAfterFrame();
        }
    }

    void Draw(Gui gui)
    {
        if (gui.Pass == Pass.Pass1Build)
        {
            app.LivePlugins?.Refresh();
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
                if (paletteOpen)
                {
                    CommandPalette(gui);
                }
                else
                {
                    var guarded = SnapshotGuarded(gui);
                    gui.DockSpace(frameSlot.Layout, PanelInfo, RenderPanel, RenderTabStripActions);
                    ReviewClosed(gui, guarded);
                }
            }

            StatusBar(gui);
        }

        RenderOverlays(gui);

        // Panels queue notifications while drawing; they show above the panels and below a blocking task.
        gui.Toasts();

        RenderBlocker(gui);

        if (gui.Pass != Pass.Pass2Render)
        {
            return;
        }

        SaveLayoutIfChanged();
        app.Settings.Flush();

        // A theme preview lasts one frame: whatever renewed it above has just drawn, so anything that
        // did not is dropped here and the committed theme comes back.
        app.Themes.EndFrame();
        TickPlugins(gui.Time.DeltaTime);
    }
}
