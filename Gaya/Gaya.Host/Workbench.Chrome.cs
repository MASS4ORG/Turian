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
        var entries = items.Select(item => (Item: item, Submenu: SubmenuAt(item.Path, depth))).ToArray();
        var nested = GroupSubmenus(entries);
        var submenus = new HashSet<string>(StringComparer.Ordinal);
        string? previousGroup = null;

        // A submenu sits where its first contribution sorts.
        foreach (var (item, submenu) in entries)
        {
            var command = submenu is null ? app.Commands.Find(item.CommandId) : null;
            if (submenu is null ? command is null : !submenus.Add(submenu))
            {
                continue;
            }

            if (previousGroup is not null && item.Group != previousGroup)
            {
                builder.Separator();
            }

            previousGroup = item.Group;

            if (submenu is not null)
            {
                builder.Submenu(submenu, sub => BuildItems(sub, [.. nested[submenu]], depth + 1));
                continue;
            }

            AddMenuCommand(builder, command!);
        }
    }

    /// <summary>Groups contributions by submenu while preserving their registry order.</summary>
    static ILookup<string?, MenuItemDescriptor> GroupSubmenus(
        IEnumerable<(MenuItemDescriptor Item, string? Submenu)> entries) =>
        entries.ToLookup(static entry => entry.Submenu, static entry => entry.Item, StringComparer.Ordinal);

    /// <summary>Adds a command row with its current label, shortcut and enabled state.</summary>
    void AddMenuCommand(FlyoutBuilder builder, CommandDescriptor command) =>
        builder.Item(command.MenuLabelFor(app.Services), () => ExecuteCommand(command.Id),
            app.Shortcuts.DisplayFor(command.Id), command.CanExecute?.Invoke(app.Services) ?? true);

    /// <summary>The submenu title at the current menu level, if any.</summary>
    static string? SubmenuAt(string path, int depth)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return depth < segments.Length ? segments[depth] : null;
    }

    /// <summary>
    /// Every registered panel the active layout may hold as a check item, ticked while the panel is docked, then the
    /// theme list and whatever the plugins contributed to the View menu.
    /// </summary>
    void BuildViewMenu(FlyoutBuilder builder)
    {
        foreach (var descriptor in app.Panels.All.Where(d => d.AllowedIn(activeSlot.Kind)))
        {
            var panelId = descriptor.Id;
            builder.CheckItem(T(descriptor.Title), () => Layout.Contains(panelId), _ => TogglePanel(panelId));
        }

        builder.Separator();
        builder.Submenu(T("Looks"), BuildLookMenu);
        builder.Submenu(T("Themes"), BuildThemeMenu);
        builder.Submenu(T("Icons"), BuildIconMenu);

        if (!app.Menus.ItemsFor(MenuIds.View).Any())
        {
            return;
        }

        builder.Separator();
        BuildItems(builder, [.. app.Menus.ItemsFor(MenuIds.View)], depth: 0);
    }

    /// <summary>
    /// The themes as check items. Resting on a row previews it — the preview is renewed for as long as
    /// the pointer stays, and dropped by <c>EndFrame</c> once it leaves — and clicking commits it.
    /// </summary>
    void BuildThemeMenu(FlyoutBuilder builder)
    {
        foreach (var theme in app.Themes.ColorThemes)
        {
            var id = theme.Id;
            builder.CheckItem(theme.Name,
                () => string.Equals(app.Themes.CommittedColorTheme, id, StringComparison.OrdinalIgnoreCase),
                _ => app.Themes.ApplyColorTheme(id),
                onHover: () => app.Themes.PreviewColorTheme(id));
        }
    }

    /// <summary>The looks as check items; committing one also applies the color theme it suggests.</summary>
    void BuildLookMenu(FlyoutBuilder builder)
    {
        foreach (var look in app.Themes.Looks)
        {
            var id = look.Id;
            builder.CheckItem(look.Name,
                () => string.Equals(app.Themes.CommittedLook, id, StringComparison.OrdinalIgnoreCase),
                _ =>
                {
                    var suggested = app.Themes.DefaultColorThemeFor(id);
                    app.Themes.ApplyLook(id);
                    if (suggested is not null)
                    {
                        app.Themes.ApplyColorTheme(suggested);
                    }
                },
                onHover: () => app.Themes.PreviewLook(id));
        }
    }

    /// <summary>The icon themes as check items, previewed on hover like the color themes.</summary>
    void BuildIconMenu(FlyoutBuilder builder)
    {
        foreach (var icons in app.Themes.IconThemes)
        {
            var id = icons.Id;
            builder.CheckItem(icons.Name,
                () => string.Equals(app.Themes.CommittedIconTheme, id, StringComparison.OrdinalIgnoreCase),
                _ => app.Themes.ApplyIconTheme(id),
                onHover: () => app.Themes.PreviewIconTheme(id));
        }
    }

    /// <summary>
    /// Brings a panel to the front, opening it at its declared placement when it was closed. What a
    /// command such as Edit / Settings calls to show its panel.
    /// </summary>
    /// <param name="panelId">The panel to show.</param>
    public void ShowPanel(string panelId)
    {
        if (!descriptors.TryGetValue(panelId, out var descriptor) || !descriptor.AllowedIn(activeSlot.Kind))
        {
            return;
        }

        if (!Layout.Contains(panelId))
        {
            Place(activeSlot, descriptor);
        }

        Layout.Activate(panelId);
    }

    /// <summary>Opens the command palette, or closes it when it is already open.</summary>
    public void ToggleCommandPalette() => paletteOpen = !paletteOpen;

    /// <summary>Opens a closed panel at its declared placement, or closes an open one.</summary>
    /// <param name="panelId">The panel to toggle.</param>
    public void TogglePanel(string panelId)
    {
        if (!descriptors.TryGetValue(panelId, out var descriptor) || !descriptor.AllowedIn(activeSlot.Kind))
        {
            return;
        }

        if (Layout.Contains(panelId))
        {
            ClosePanel(panelId);
        }
        else
        {
            Place(activeSlot, descriptor);
        }
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
                            if (hot)
                            {
                                gui.DrawBackgroundRect(t.Hover, 3f);
                            }

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
        var items = ChromeFor(ChromeSlot.StatusBar);
        var height = t.Scale(items.Count == 0
            ? t.StatusHeight
            : Math.Max(t.StatusHeight, items.Max(item => item.Height > 0 ? item.Height : t.StatusHeight)));

        using (gui.Node(-1, height).ExpandWidth().Direction(Axis.Horizontal).Gap(t.Gap).Enter())
        {
            gui.DrawBackgroundRect(t.Chrome);

            foreach (var item in items)
            {
                using (gui.Node(-1, height, $"chrome/StatusBar/{item.Id}").ExpandWidth(item.Weight).ExpandHeight().Enter())
                {
                    ResolveChrome(item).Render(gui);
                }
            }

            using (gui.Node(t.Scale(statusSummaryWidth), height, "chrome/StatusBar/readout").AlignContentY(0.5f)
                       .AlignContentX(1f).Padding(t.Gap, 0f).Enter())
            {
                gui.DrawText(StatusReadout.Format(gui.Time.SmoothFps, MemoryBytes(gui.Time.Elapsed)),
                    t.Text(12), t.InkDim);
            }
        }
    }

    /// <summary>The studio process's resident memory, sampled about once a second because reading it is not free.</summary>
    long MemoryBytes(float now)
    {
        if (now - memorySampledAt >= 1f || memoryBytes == 0)
        {
            memoryBytes = Environment.WorkingSet;
            memorySampledAt = now;
        }

        return memoryBytes;
    }

    long memoryBytes;
    float memorySampledAt;

    /// <summary>Width reserved on the right of the status bar for the workbench's own readout.</summary>
    const float statusSummaryWidth = 300f;

}
