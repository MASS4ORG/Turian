namespace Gaya.Packages.Editor;

/// <summary>
/// The Bricks panel: one searchable list of every brick the studio or the open workspace could use, grouped by how
/// far it is from being used (in use, on this machine, available), and a second tab for the registries bricks come
/// from. A checkbox turns a brick on or off; selecting a row shows the brick or registry in the inspector, which is
/// where its details and actions are. It draws <see cref="BricksController"/> and holds no logic of its own.
/// </summary>
/// <param name="controller">The bricks' state and actions.</param>
/// <param name="dialogs">Asks for a folder or file to add a brick from; without it those menu items are inert.</param>
/// <param name="inspector">Shows the selected brick or registry; without it selecting only highlights the row.</param>
public sealed class BricksPanel(BricksController controller, IBrickFileDialogs? dialogs = null,
    IBrickInspector? inspector = null) : IPanel
{
    /// <summary>The panel's id; File and Settings entries bring it to the front.</summary>
    public const string PanelId = "gaya.bricks";

    const float rowHeight = 36f;

    static readonly (BrickCategoryFilter Category, string Label, float Width)[] Categories =
    [
        (BrickCategoryFilter.All, "Everything", 72f),
        (BrickCategoryFilter.Looks, "Looks", 48f),
        (BrickCategoryFilter.Themes, "Themes", 58f),
        (BrickCategoryFilter.IconThemes, "Icon Themes", 82f),
        (BrickCategoryFilter.Fonts, "Fonts", 48f),
    ];

    static readonly (BrickFilter Filter, string Label, float Width)[] Filters =
    [
        (BrickFilter.All, "All", 36f),
        (BrickFilter.Installed, "Installed", 62f),
        (BrickFilter.Available, "Available", 66f),
        (BrickFilter.Updates, "Updates", 58f),
        (BrickFilter.BuiltIn, "Built-in", 58f),
    ];

    string search = "";
    string addInput = "";
    string? addLabel;
    Func<string, Task<bool>>? addAction;
    BrickFilter filter = BrickFilter.All;
    bool loaded;
    bool scopeChosen;
    bool addMenuOpen;
    bool moreMenuOpen;
    Vector2 pointer;
    Vector2 menuAt;
    int seenRevision = -1;

    static ThemeTokens Theme => ThemeTokens.Current;

    /// <summary>The content category the brick list shows, such as themes only.</summary>
    public BrickCategoryFilter Category { get; set; }

    /// <summary>Shows the studio's bricks of one content category, as Settings' "Browse themes…" does.</summary>
    /// <param name="category">The category to list.</param>
    public void Browse(BrickCategoryFilter category)
    {
        scopeChosen = true;
        controller.Scope = BrickScope.Studio;
        controller.Tab = BricksTab.Bricks;
        Category = category;
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        if (!loaded)
        {
            loaded = true;
            if (!scopeChosen && !controller.HasProject && controller.HasStudio) controller.Scope = BrickScope.Studio;
            controller.Refresh();
            _ = controller.RefreshRegistriesAsync();
        }

        pointer = gui.Input.MousePosition;
        if (controller.Revision != seenRevision)
        {
            seenRevision = controller.Revision;
            inspector?.Refresh(controller);
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(Theme.Gap).Padding(10f, 8f).Enter())
        {
            Scopes(gui);
            if (!controller.HasWorkspace)
            {
                Line(gui, controller.Scope == BrickScope.Project
                    ? "Open a project to manage its bricks."
                    : "This application has no studio bricks.", Theme.InkDim);
                return;
            }

            Tabs(gui);
            Notice(gui);

            if (controller.Tab == BricksTab.Registries)
            {
                Registries(gui);
                return;
            }

            Toolbar(gui);
            if (addLabel is not null) AddRow(gui);
            List(gui);
        }

        gui.CascadeMenu(ref addMenuOpen, menuAt, BuildAddMenu);
        gui.CascadeMenu(ref moreMenuOpen, menuAt, BuildMoreMenu);
    }

    void Show(Action select)
    {
        select();
        inspector?.ShowSelection(controller);
    }

    /// <summary>The Studio and Project scope chips, shown when the host has both.</summary>
    void Scopes(Gui gui)
    {
        if (!controller.HasStudio) return;

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/scopes").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            foreach (var (scope, label, width) in new[] { (BrickScope.Studio, "Studio", 56f), (BrickScope.Project, "Project", 60f) })
            {
                if (!Chip(gui, label, $"bricks/scope/{scope}", width, controller.Scope == scope) || controller.Scope == scope)
                    continue;
                scopeChosen = true;
                controller.Scope = scope;
                _ = controller.RefreshRegistriesAsync();
                inspector?.ShowSelection(controller);
            }
        }
    }

    void Tabs(Gui gui)
    {
        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/tabs").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            foreach (var (tab, label, width) in new[] { (BricksTab.Bricks, "Bricks", 56f), (BricksTab.Registries, "Registries", 76f) })
            {
                if (Chip(gui, label, $"bricks/tab/{tab}", width, controller.Tab == tab) && controller.Tab != tab)
                    Show(() => controller.Tab = tab);
            }
        }
    }

    void Registries(Gui gui)
    {
        var height = Theme.Scale(rowHeight);

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight + 4f), "bricks/registries/toolbar").ExpandWidth()
                   .Direction(Axis.Horizontal).Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            if (Button(gui, "+", "bricks/registries/add", 28f, "Add a registry to take bricks from."))
                Show(() => controller.SelectedRegistry = "");
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
        {
            gui.ScrollY();
            foreach (var registry in controller.Registries)
                RegistryRow(gui, registry, height);
        }
    }

    void RegistryRow(Gui gui, ScopedRegistry registry, float height)
    {
        var id = $"bricks/registry/{registry.Name}";
        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).Padding(6f, 0f)
                   .ContentAlignY(0.5f).Enter())
        {
            Selectable(gui, controller.SelectedRegistry == registry.Name, () => controller.SelectedRegistry = registry.Name);

            using (gui.Node(-1, height, $"{id}/name").Expand().Direction(Axis.Vertical).Gap(2f).ContentAlignY(0.5f).Enter())
            {
                gui.ClipContent();
                gui.DrawText(registry.Name, Theme.Text(12f), Theme.Ink, centerInRect: false);
                gui.DrawText(registry.Url, Theme.Text(10f), Theme.InkDim, centerInRect: false);
            }

            Cell(gui, $"{id}/scopes", string.Join(", ", registry.Scopes), 150f, height);
        }
    }

    /// <summary>Highlights the row being drawn and runs <paramref name="select"/> when it is clicked.</summary>
    void Selectable(Gui gui, bool selected, Action select)
    {
        var interactable = gui.GetInteractable();
        if (gui.Pass != Pass.Pass2Render) return;

        if (selected) gui.DrawBackgroundRect(Theme.Hover, 3f);
        else if (interactable.OnHover()) gui.DrawBackgroundRect(Theme.Chrome, 3f);
        if (interactable.OnClick()) Show(select);
    }

    void Toolbar(Gui gui)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);

        using (gui.Node(-1, height, "bricks/toolbar").ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Gap)
                   .ContentAlignY(0.5f).Enter())
        {
            if (Button(gui, "+", "bricks/add", 28f, "Add a brick by name, from a git repository, a folder or a .brick file."))
                OpenMenu(ref addMenuOpen);

            using (gui.Node(-1, height, "bricks/search").Expand().Enter())
                search = gui.TextInput(search, width: 0, height: height, placeholder: "Search bricks",
                    fontSize: Theme.Text(12), padding: 5, id: "bricks/search/input");

            if (Button(gui, "…", "bricks/more", 28f, "Refresh the registries, restore the project's bricks, or update them all."))
                OpenMenu(ref moreMenuOpen);
        }

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/filters").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            foreach (var (value, label, width) in Filters)
            {
                if (Chip(gui, label, $"bricks/filter/{value}", width, filter == value)) filter = value;
            }
        }

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/categories").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            foreach (var (value, label, width) in Categories)
            {
                if (Chip(gui, label, $"bricks/category/{value}", width, Category == value)) Category = value;
            }
        }
    }

    void OpenMenu(ref bool open)
    {
        menuAt = pointer;
        open = true;
    }

    void BuildAddMenu(FlyoutBuilder menu)
    {
        menu.Item("Add by name…", () => BeginAdd("Brick name, e.g. org.mass4.themes.nord", AddByName));
        menu.Item("Add from git URL…", () => BeginAdd("Git repository, optionally ending in #tag or #branch",
            url => controller.AddFromSourceAsync($"git+{url.Trim()}")));
        menu.Separator();
        menu.Item("Add from folder…", () => Pick(folder: true, "Add a brick from a folder"), enabled: dialogs is not null);
        menu.Item("Add from .brick file…", () => Pick(folder: false, "Add a brick from a .brick file"),
            enabled: dialogs is not null);
    }

    void BuildMoreMenu(FlyoutBuilder menu)
    {
        menu.Item("Bricks settings", () => inspector?.ShowSettings(controller));
        menu.Item("Refresh registries", () => _ = controller.RefreshRegistriesAsync());
        menu.Item("Restore", () => _ = controller.RestoreAsync());
        menu.Item("Update all", () => _ = controller.UpdateAsync());
    }

    void BeginAdd(string label, Func<string, Task<bool>> action)
    {
        addLabel = label;
        addAction = action;
        addInput = "";
    }

    Task<bool> AddByName(string name)
    {
        var id = name.Trim();
        return controller.Catalog.FirstOrDefault(b => b.Id == id) is { } known
            ? controller.EnableAsync(known)
            : controller.InstallAsync(id, null);
    }

    void Pick(bool folder, string title) =>
        dialogs?.Pick(folder, title, path =>
        {
            if (path is not null) _ = controller.AddFromSourceAsync($"file:{path}");
        });

    void AddRow(Gui gui)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);

        using (gui.Node(-1, height, "bricks/addRow").ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Gap).Enter())
        {
            using (gui.Node(-1, height, "bricks/addRow/input").Expand().Enter())
                addInput = gui.TextInput(addInput, width: 0, height: height, placeholder: addLabel ?? "",
                    fontSize: Theme.Text(12), padding: 5, id: "bricks/addRow/text");

            if (Button(gui, "Add", "bricks/addRow/add", 50f) && addInput.Trim().Length > 0)
            {
                _ = addAction?.Invoke(addInput);
                addLabel = null;
            }

            if (Button(gui, "Cancel", "bricks/addRow/cancel", 60f)) addLabel = null;
        }
    }

    void Notice(Gui gui)
    {
        if (controller.IsBusy) Line(gui, "Working…", Theme.InkDim);
        else if (controller.Error is { } error) Line(gui, error, Theme.Error);
    }

    void List(Gui gui)
    {
        var shown = BrickCatalog.Filter(controller.Catalog, filter, search, Category);
        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
        {
            gui.ScrollY();
            Group(gui, "In use", shown.Where(static b => b.State == BrickState.Enabled));
            Group(gui, "On this machine", shown.Where(static b => b.State == BrickState.Installed));
            Group(gui, "Available", shown.Where(static b => b.State == BrickState.Available));
            if (shown.Count == 0) Line(gui, "No bricks match.", Theme.InkDim);
        }
    }

    void Group(Gui gui, string title, IEnumerable<CatalogBrick> bricks)
    {
        var rows = bricks.ToList();
        if (rows.Count == 0) return;

        Line(gui, $"{title} ({rows.Count})", Theme.Ink);
        foreach (var brick in rows) Row(gui, brick);
    }

    void Row(Gui gui, CatalogBrick brick)
    {
        var id = $"bricks/row/{brick.Id}";
        var height = Theme.Scale(rowHeight);
        var selected = controller.Selected == brick.Id;

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).Padding(6f, 0f)
                   .ContentAlignY(0.5f).Enter())
        {
            Selectable(gui, selected, () => controller.Selected = brick.Id);

            var enabled = brick.State == BrickState.Enabled;
            using (gui.Node(Theme.Scale(18f), height, $"{id}/enabled").ContentAlignY(0.5f).Enter())
            {
                if (gui.Checkbox(enabled, size: Theme.Scale(14f)) != enabled && !controller.IsBusy)
                    _ = enabled ? controller.DisableAsync(brick.Id) : controller.EnableAsync(brick);
            }

            using (gui.Node(-1, height, $"{id}/name").Expand().Direction(Axis.Vertical).Gap(2f).ContentAlignY(0.5f).Enter())
            {
                gui.ClipContent();
                gui.DrawText(brick.DisplayName is { Length: > 0 } name ? name : brick.Id, Theme.Text(12f),
                    enabled ? Theme.Ink : Theme.InkDim, centerInRect: false);
                gui.DrawText(brick.Id, Theme.Text(10f), Theme.InkDim, centerInRect: false);
            }

            var version = brick.InstalledVersion ?? brick.LatestVersion ?? "";
            Cell(gui, $"{id}/version", brick.HasUpdate ? $"{version} → {brick.LatestVersion}" : version,
                brick.HasUpdate ? 110f : 60f, height);
        }
    }

    static void Line(Gui gui, string text, Color color)
    {
        using (gui.Node(-1, Theme.Scale(rowHeight - 6f), $"bricks/line/{text}").ExpandWidth().ContentAlignY(0.5f).Enter())
        {
            gui.ClipContent();
            gui.DrawText(text, Theme.Text(11f), color, centerInRect: false);
        }
    }

    static void Cell(Gui gui, string id, string text, float width, float height)
    {
        using (gui.Node(Theme.Scale(width), height, id).ContentAlignY(0.5f).Enter())
        {
            gui.ClipContent();
            gui.DrawText(text, Theme.Text(11f), Theme.InkDim, centerInRect: false);
        }
    }

    static bool Chip(Gui gui, string label, string id, float width, bool active)
    {
        var height = Theme.Scale(Theme.RowHeight);

        using (gui.Node(Theme.Scale(width), height, id).BlockInput().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
                gui.DrawBackgroundRect(active ? Theme.Hover : hot ? Theme.Chrome : Theme.Panel, 9f);
            gui.DrawText(label, Theme.Text(11f), active || hot ? Theme.Ink : Theme.InkDim);

            return gui.Pass == Pass.Pass2Render && interactable.OnClick();
        }
    }

    bool Button(Gui gui, string label, string id, float width, string? tooltip = null) =>
        SmallTextButton(gui, label, id, Theme.Scale(width), tooltip) && !controller.IsBusy;

    static bool SmallTextButton(Gui gui, string label, string id, float width, string? tooltip)
    {
        using (gui.Node(width, Theme.Scale(Theme.RowHeight), id).BlockInput().ContentAlignX(0.5f).ContentAlignY(0.5f)
                   .Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(hot ? Theme.Hover : Theme.Chrome, 3f);
            gui.DrawText(label, Theme.Text(11f), hot ? Theme.Ink : Theme.InkDim);
            if (tooltip is not null) gui.Tooltip(gui.CurrentNode, tooltip, maxWidth: 320);

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }
}
