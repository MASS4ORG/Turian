namespace Gaya.Plugin.Turian;

/// <summary>
/// The Bricks panel: one searchable list of every brick the project could use, grouped by how far it is from being
/// used (in use, on this machine, available), and the selected brick's details beside it. A checkbox turns a brick
/// on or off for the project. It draws <see cref="BricksController"/> and holds no logic of its own.
/// </summary>
/// <param name="controller">The bricks' state and actions.</param>
/// <param name="dialogs">Asks for a folder or file to add a brick from; without it those menu items are inert.</param>
sealed class BricksPanel(BricksController controller, FileDialogChrome? dialogs = null) : IPanel
{
    const float rowHeight = 36f;

    static readonly (BrickFilter Filter, string Label, float Width)[] filters =
    [
        (BrickFilter.All, "All", 36f),
        (BrickFilter.Installed, "Installed", 62f),
        (BrickFilter.Available, "Available", 66f),
        (BrickFilter.Updates, "Updates", 58f),
        (BrickFilter.BuiltIn, "Built-in", 58f),
    ];

    readonly BricksDetails details = new(controller);

    string search = "";
    string addInput = "";
    string? addLabel;
    Func<string, Task<bool>>? addAction;
    BrickFilter filter = BrickFilter.All;
    bool loaded;
    bool addMenuOpen;
    bool moreMenuOpen;
    Vector2 pointer;
    Vector2 menuAt;
    float split = 0.45f;

    static StudioTheme Theme => StudioTheme.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        if (!loaded)
        {
            loaded = true;
            controller.Refresh();
            _ = controller.RefreshRegistriesAsync();
        }

        pointer = gui.Input.MousePosition;

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(Theme.Gap).Padding(10f, 8f).Enter())
        {
            if (!controller.HasProject)
            {
                BricksDetails.Line(gui, "Open a project to manage its bricks.", Theme.InkDim);
                return;
            }

            Toolbar(gui);
            if (addLabel is not null) AddRow(gui);
            Notice(gui);

            using (gui.Node().Expand().Direction(Axis.Horizontal).Enter())
            {
                using (gui.Node().ExpandHeight().ExpandWidth(split).Direction(Axis.Vertical).Enter())
                    List(gui);

                gui.Splitter(ref split, Axis.Horizontal, thickness: Theme.Scale(2f), min: 0.25f,
                    color: Theme.Border, hoverColor: Theme.Hover);

                using (gui.Node().ExpandHeight().ExpandWidth(1f - split).Direction(Axis.Vertical).Padding(8f, 0f).Enter())
                    details.Render(gui);
            }
        }

        gui.CascadeMenu(ref addMenuOpen, menuAt, BuildAddMenu);
        gui.CascadeMenu(ref moreMenuOpen, menuAt, BuildMoreMenu);
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
            foreach (var (value, label, width) in filters)
            {
                if (Chip(gui, label, $"bricks/filter/{value}", width, filter == value)) filter = value;
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
        menu.Item("Add by name…", () => BeginAdd("Brick name, e.g. org.mass4.turian.ui", AddByName));
        menu.Item("Add from git URL…", () => BeginAdd("Git repository, optionally ending in #tag or #branch",
            url => controller.AddFromSourceAsync($"git+{url.Trim()}")));
        menu.Separator();
        menu.Item("Add from folder…", () => Pick(FileDialogMode.SelectFolder, "Add a brick from a folder", []),
            enabled: dialogs is not null);
        menu.Item("Add from .brick file…", () => Pick(FileDialogMode.OpenFile, "Add a brick from a .brick file",
            [FileDialogFilter.Of("Bricks", ".brick")]), enabled: dialogs is not null);
    }

    void BuildMoreMenu(FlyoutBuilder menu)
    {
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

    void Pick(FileDialogMode mode, string title, IReadOnlyList<FileDialogFilter> accepted) =>
        dialogs?.Show(new FileDialogRequest
        {
            Mode = mode,
            Title = title,
            Filters = accepted,
            OnComplete = path =>
            {
                if (path is not null) _ = controller.AddFromSourceAsync($"file:{path}");
            },
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
        if (controller.IsBusy) BricksDetails.Line(gui, "Working…", Theme.InkDim);
        else if (controller.Error is { } error) BricksDetails.Line(gui, error, Theme.Error);
    }

    void List(Gui gui)
    {
        var shown = BrickCatalog.Filter(controller.Catalog, filter, search);
        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
        {
            gui.ScrollY();
            Group(gui, "In use", shown.Where(static b => b.State == BrickState.Enabled));
            Group(gui, "On this machine", shown.Where(static b => b.State == BrickState.Installed));
            Group(gui, "Available", shown.Where(static b => b.State == BrickState.Available));
            if (shown.Count == 0) BricksDetails.Line(gui, "No bricks match.", Theme.InkDim);
        }
    }

    void Group(Gui gui, string title, IEnumerable<CatalogBrick> bricks)
    {
        var rows = bricks.ToList();
        if (rows.Count == 0) return;

        BricksDetails.Line(gui, $"{title} ({rows.Count})", Theme.Ink);
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
            var interactable = gui.GetInteractable();
            if (gui.Pass == Pass.Pass2Render)
            {
                if (selected) gui.DrawBackgroundRect(Theme.Hover, 3f);
                else if (interactable.OnHover()) gui.DrawBackgroundRect(Theme.Chrome, 3f);
                if (interactable.OnClick()) controller.Selected = brick.Id;
            }

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
            BricksDetails.Cell(gui, $"{id}/version", brick.HasUpdate ? $"{version} → {brick.LatestVersion}" : version,
                brick.HasUpdate ? 110f : 60f, height);
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
        StudioControls.SmallTextButton(gui, label, id, Theme.Scale(width), tooltip) && !controller.IsBusy;
}
