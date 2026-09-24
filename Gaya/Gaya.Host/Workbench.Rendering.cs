namespace Gaya.Host;

/// <summary>
/// The application shell. Owns nothing product-specific: it renders a menu bar, a dock space and a
/// status bar entirely from the contribution registries in a <see cref="GayaApplication"/>. Panels
/// are instantiated once via their factories and cached.
/// </summary>
public sealed partial class Workbench
{

    /// <summary>
    /// Advances every plugin once per drawn frame. A play session has to keep running while its
    /// panel is behind another dock tab, so the pump lives here rather than in a panel.
    /// </summary>
    void TickPlugins(float deltaTime)
    {
        foreach (var plugin in app.Plugins)
        {
            try
            {
                plugin.Tick(app.Services, deltaTime);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Plugin {Plugin} threw while ticking", plugin.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Draws a slot's chrome contributions in one strip. The strip is skipped entirely when nothing is
    /// registered, so a bare workbench has no empty band under its menu.
    /// </summary>
    void ChromeStrip(Gui gui, ChromeSlot slot)
    {
        var items = app.Chrome.For(slot).ToList();
        if (items.Count == 0) return;

        var height = Theme.Scale(items.Max(item => item.Height > 0 ? item.Height : Theme.HeaderHeight));

        using (gui.Node(-1, height, $"chrome/{slot}").ExpandWidth().Direction(Axis.Horizontal).Enter())
        {
            foreach (var item in items)
                using (gui.Node(-1, height, $"chrome/{slot}/{item.Id}").Expand().Enter())
                    ResolveChrome(item).Render(gui);
        }
    }

    IChromeItem ResolveChrome(ChromeDescriptor descriptor)
    {
        if (chromeInstances.TryGetValue(descriptor.Id, out var item)) return item;

        try
        {
            item = descriptor.Factory(app.Services);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Chrome {ChromeId} could not be created", descriptor.Id);
            item = new BrokenChrome(descriptor.Id);
        }

        chromeInstances[descriptor.Id] = item;
        return item;
    }

    /// <summary>
    /// Draws into the width a dock group's tabs leave over whatever its active panel registered — the
    /// output console's settings menu is the one consumer today. Called from both passes so the node
    /// the dock reserves for the actions exists even when no panel contributes one.
    /// </summary>
    void RenderTabStripActions(DockTabStrip strip, Gui gui)
    {
        var panelId = strip.ActivePanelId;
        if (panelId is null) return;

        foreach (var item in app.TabStripChrome.For(panelId))
            ResolveTabStripChrome(item).Render(gui);
    }

    IChromeItem ResolveTabStripChrome(TabStripChromeDescriptor descriptor)
    {
        if (tabStripChromeInstances.TryGetValue(descriptor.Id, out var item)) return item;

        try
        {
            item = descriptor.Factory(app.Services);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tab strip chrome {ChromeId} could not be created", descriptor.Id);
            item = new BrokenChrome(descriptor.Id);
        }

        tabStripChromeInstances[descriptor.Id] = item;
        return item;
    }

    /// <summary>Stands in for chrome whose factory threw, matching how a broken panel is handled.</summary>
    sealed class BrokenChrome(string chromeId) : IChromeItem
    {
        public void Render(Gui gui) =>
            gui.DrawText($"{chromeId} failed to load — see the log.", StudioTheme.Current.Text(12),
                StudioTheme.Current.Error);
    }

    DockPanelInfo? PanelInfo(string panelId) =>
        descriptors.TryGetValue(panelId, out var descriptor) ? new DockPanelInfo(T(descriptor.Title)) : null;

    void RenderPanel(string panelId, Gui gui)
    {
        if (!descriptors.TryGetValue(panelId, out var descriptor)) return;

        // No padding here: a panel that scrolls wants its scrollbar on its own border, so each panel
        // pads its own content instead.
        using (gui.Node().Expand().Enter())
        {
            TrackFocus(gui, panelId);
            Resolve(descriptor).Render(gui);
        }
    }

    /// <summary>
    /// Focus follows the pointer press, the way an IDE's panels do: pressing anywhere inside a panel
    /// makes it the one panel-scoped shortcuts are matched against.
    /// </summary>
    void TrackFocus(Gui gui, string panelId)
    {
        if (gui.Pass != Pass.Pass2Render) return;
        if (!Pressed(gui.Input)) return;
        if (!gui.CurrentNode.Rect.Contains(gui.Input.MousePosition)) return;

        app.Focus.Focus(panelId);
    }

    static bool Pressed(IInputHandler input) =>
        input.IsMouseButtonPressed(MouseButton.Left)
        || input.IsMouseButtonPressed(MouseButton.Right)
        || input.IsMouseButtonPressed(MouseButton.Middle);

    /// <inheritdoc />
    public IPanel? Panel(string panelId) =>
        descriptors.TryGetValue(panelId, out var descriptor) ? Resolve(descriptor) : null;

    /// <inheritdoc />
    public string Title(string panelId) =>
        descriptors.TryGetValue(panelId, out var descriptor) ? T(descriptor.Title) : panelId;

    /// <summary>
    /// Dispatches this frame's key press. A focused text field swallows it, so typing an <c>s</c> into
    /// a rename box never also saves; Escape cancels an armed chord and closes the palette.
    /// </summary>
    void HandleShortcuts(Gui gui)
    {
        if (gui.Pass != Pass.Pass2Render) return;

        var input = gui.Input;
        if (input.IsKeyPressed(KeyboardKey.Escape))
        {
            app.Shortcuts.CancelPending();
            paletteOpen = false;
        }

        if (app.Shortcuts.IsCapturing || gui.Focus.IsTextInputFocused) return;

        if (app.Shortcuts.Dispatch(input, app.Focus.ActivePanelId) is { } commandId)
            ExecuteCommand(commandId);
    }

    void MenuBar(Gui gui)
    {
        var t = Theme;
        var height = t.Scale(t.MenuHeight);

        using (gui.Node(-1, height, "menubar").ExpandWidth().Direction(Axis.Horizontal).Enter())
        {
            gui.DrawBackgroundRect(t.Chrome);

            using (gui.Node().Expand().Enter())
                gui.MenuBar(BuildMenus, height, t.Chrome, t.Ink, t.Hover, t.Text(13f), padding: 10f);

            // The menus expand, so whatever a plugin puts here lands against the right edge.
            foreach (var item in app.Chrome.For(ChromeSlot.MenuBar))
                using (gui.Node(-1, height, $"chrome/MenuBar/{item.Id}")
                           .Direction(Axis.Horizontal).ContentAlignY(0.5f).Enter())
                    ResolveChrome(item).Render(gui);
        }
    }

    void BuildMenus(MenuBarBuilder bar)
    {
        foreach (var (menuId, label) in TopLevelMenus())
        {
            var id = menuId;
            bar.Menu(label, menu =>
            {
                if (id == MenuIds.View) BuildViewMenu(menu);
                else BuildItems(menu, [.. app.Menus.ItemsFor(id)], depth: 0);
            });
        }
    }

    /// <summary>The View menu is always offered: the workbench fills it with the panel list itself.</summary>
    bool HasItems(string menuId) => menuId == MenuIds.View || app.Menus.ItemsFor(menuId).Any();

    /// <summary>
    /// The menu titles to draw: the well-known ones that have items, in their fixed order, then any a
    /// plugin declared — which is how a user-code <c>Tools</c> menu appears.
    /// </summary>
    IEnumerable<(string MenuId, string Label)> TopLevelMenus()
    {
        foreach (var menuId in menuOrder.Where(HasItems))
            yield return (menuId, T(menuLabels[menuId]));

        foreach (var (menuId, label) in app.Menus.DeclaredMenus)
            if (!menuLabels.ContainsKey(menuId) && HasItems(menuId))
                yield return (menuId, T(label));
    }

}
