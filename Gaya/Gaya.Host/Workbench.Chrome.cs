namespace Gaya.Host;

/// <summary>
/// The application shell. Owns nothing product-specific: it renders a menu bar, a dock space and a
/// status bar entirely from the contribution registries in a <see cref="GayaApplication"/>. Panels
/// are instantiated once via their factories and cached.
/// </summary>
public sealed partial class Workbench
{
    void BuildItems(FlyoutBuilder builder, IReadOnlyList<MenuItemDescriptor> items, int depth)
    {
        string? previousGroup = null;

        foreach (var item in items.Where(item => SegmentsFrom(item, depth).Length == 0))
        {
            if (!app.Commands.TryGet(item.CommandId, out var command)) continue;

            if (previousGroup is not null && item.Group != previousGroup) builder.Separator();
            previousGroup = item.Group;

            var commandId = item.CommandId;
            builder.Item(
                command.MenuLabelFor(app.Services),
                () => ExecuteCommand(commandId),
                app.Shortcuts.DisplayFor(commandId),
                command.CanExecute?.Invoke(app.Services) ?? true);
        }

        foreach (var group in items.Where(item => SegmentsFrom(item, depth).Length > 0)
                     .GroupBy(item => SegmentsFrom(item, depth)[0], StringComparer.Ordinal))
        {
            var nested = group.ToList();
            builder.Submenu(group.Key, sub => BuildItems(sub, nested, depth + 1));
        }
    }

    /// <summary>The part of an item's submenu path still to be walked at this depth.</summary>
    static string[] SegmentsFrom(MenuItemDescriptor item, int depth)
    {
        if (item.Path.Length == 0) return [];

        var segments = item.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return depth >= segments.Length ? [] : segments[depth..];
    }

    /// <summary>
    /// Every registered panel as a check item, ticked while the panel is docked, then the theme list
    /// and whatever the plugins contributed to the View menu.
    /// </summary>
    void BuildViewMenu(FlyoutBuilder builder)
    {
        foreach (var descriptor in app.Panels.All)
        {
            var panelId = descriptor.Id;
            builder.CheckItem(T(descriptor.Title), () => Layout.Contains(panelId), _ => TogglePanel(panelId));
        }

        builder.Separator();
        builder.Submenu(T("Themes"), BuildThemeMenu);

        if (!app.Menus.ItemsFor(MenuIds.View).Any()) return;

        builder.Separator();
        BuildItems(builder, [.. app.Menus.ItemsFor(MenuIds.View)], depth: 0);
    }

    /// <summary>
    /// The themes as check items. Resting on a row previews it — the preview is renewed for as long as
    /// the pointer stays, and dropped by <c>EndFrame</c> once it leaves — and clicking commits it.
    /// </summary>
    void BuildThemeMenu(FlyoutBuilder builder)
    {
        foreach (var studioTheme in app.Themes.Themes)
        {
            var name = studioTheme.Name;
            builder.CheckItem(name,
                () => string.Equals(app.Themes.CommittedName, name, StringComparison.OrdinalIgnoreCase),
                _ => app.Themes.Apply(name),
                onHover: () => app.Themes.Preview(name));
        }
    }

    /// <summary>
    /// Brings a panel to the front, opening it at its declared placement when it was closed. What a
    /// command such as File / Settings calls to show its panel.
    /// </summary>
    /// <param name="panelId">The panel to show.</param>
    public void ShowPanel(string panelId)
    {
        if (!descriptors.TryGetValue(panelId, out var descriptor)) return;

        if (!Layout.Contains(panelId)) Place(Layout, descriptor);
        Layout.Activate(panelId);
    }

    /// <summary>Opens the command palette, or closes it when it is already open.</summary>
    public void ToggleCommandPalette() => paletteOpen = !paletteOpen;

    /// <summary>Opens a closed panel at its declared placement, or closes an open one.</summary>
    /// <param name="panelId">The panel to toggle.</param>
    public void TogglePanel(string panelId)
    {
        if (!descriptors.TryGetValue(panelId, out var descriptor)) return;

        if (Layout.Contains(panelId)) Layout.Remove(panelId);
        else Place(Layout, descriptor);
    }

    void CommandPalette(Gui gui)
    {
        var t = Theme;
        using (gui.Node().Expand().Direction(Axis.Vertical).Enter())
        {
            gui.DrawBackgroundRect(t.Panel, 6f);
            gui.DrawRectBorder(gui.CurrentNode.Rect, t.Border, 1f, 6f);
            var r = gui.CurrentNode.Rect;
            gui.DrawRect(new Rect(r.X, r.Y, r.W, t.Scale(t.HeaderHeight)), t.Chrome, 6f);

            using (gui.Node(r.W - 16f, r.H - 32f).Direction(Axis.Vertical).Gap(2f).Margin(8f, 6f).Enter())
            {
                gui.DrawText($"Commands ({app.Commands.All.Count})", t.Text(12), t.InkDim);
                using (gui.Node().Expand().Direction(Axis.Vertical).Enter())
                {
                    gui.ScrollY();
                    foreach (var cmd in app.Commands.All)
                    {
                        using (gui.Node(-1, t.Scale(22f)).ExpandWidth().Enter())
                        {
                            var hot = gui.GetInteractable().OnHover();
                            if (hot) gui.DrawBackgroundRect(t.Hover, 3f);
                            if (hot && gui.GetInteractable().OnClick())
                            {
                                paletteOpen = false;
                                ExecuteCommand(cmd.Id);
                            }

                            gui.DrawText(cmd.Title, t.Text(12), t.Ink);
                        }
                    }
                }
            }
        }
    }

    void StatusBar(Gui gui)
    {
        var t = Theme;
        var items = app.Chrome.For(ChromeSlot.StatusBar).ToList();
        var height = t.Scale(items.Count == 0
            ? t.StatusHeight
            : Math.Max(t.StatusHeight, items.Max(item => item.Height > 0 ? item.Height : t.StatusHeight)));

        using (gui.Node(-1, height).ExpandWidth().Direction(Axis.Horizontal).Gap(t.Gap).Enter())
        {
            gui.DrawBackgroundRect(t.Chrome);

            foreach (var item in items)
                using (gui.Node(-1, height, $"chrome/StatusBar/{item.Id}").Expand().Enter())
                    ResolveChrome(item).Render(gui);

            using (gui.Node(t.Scale(statusSummaryWidth), height).ContentAlignY(0.5f).Enter())
                gui.DrawText(
                    $"{gui.Time.SmoothFps:0} fps   ·   {app.LoadedPluginIds.Count} plugin(s)   ·   Gaya",
                    t.Text(12), t.InkDim);
        }
    }

    /// <summary>Width reserved on the right of the status bar for the workbench's own readout.</summary>
    const float statusSummaryWidth = 220f;

    IPanel Resolve(PanelDescriptor descriptor)
    {
        if (panelInstances.TryGetValue(descriptor.Id, out var panel)) return panel;

        try
        {
            panel = descriptor.Factory(app.Services);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Panel {PanelId} could not be created", descriptor.Id);
            panel = new BrokenPanel(descriptor.Id);
        }

        panelInstances[descriptor.Id] = panel;
        return panel;
    }

    /// <summary>Stands in for a panel whose factory threw, so one bad plugin cannot close the editor.</summary>
    sealed class BrokenPanel(string panelId) : IPanel
    {
        public void Render(Gui gui) =>
            gui.DrawText($"{panelId} failed to load — see the log.", StudioTheme.Current.Text(12),
                StudioTheme.Current.Error);
    }
}
