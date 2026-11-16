namespace Gaya.Packages.Editor;

/// <summary>
/// The Bricks panel: one searchable list of every brick the studio or the open project could use, grouped by how far it
/// is from being used (in use, available), and a second tab for the registries bricks come from; a Studio and Project
/// filter on the same row chooses whose. A checkbox stages turning a brick on or off, and staged changes apply together
/// so the project recompiles once. Selecting a row shows the brick or registry in the inspector, which is where its
/// details and actions are. It draws <see cref="BricksController"/> and holds no logic of its own.
/// </summary>
/// <param name="controller">The bricks' state and actions.</param>
/// <param name="dialogs">Asks for a folder or file to add a brick from; without it those menu items are inert.</param>
/// <param name="inspector">Shows the selected brick or registry; without it selecting only highlights the row.</param>
public sealed partial class BricksPanel(BricksController controller, IBrickFileDialogs? dialogs = null,
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

    string addInput = "";
    string? addLabel;
    Func<string, Task<bool>>? addAction;
    BrickFilter filter = BrickFilter.All;
    bool loaded;
    bool scopeChosen;
    bool addMenuOpen;
    bool moreMenuOpen;
    bool categoryMenuOpen;
    Vector2 pointer;
    Vector2 menuAt;
    int seenRevision = -1;
    Frame? frame;
    bool listening;
    readonly System.Collections.Concurrent.ConcurrentQueue<string> problems = new();

    static readonly ToastOptions ProblemToast = new() { Duration = 7f, MaxWidth = 480f };

    static ThemeTokens Theme => ThemeTokens.Current;

    /// <summary>The content category the brick list shows, such as themes only.</summary>
    public BrickCategoryFilter Category { get; set; }

    /// <summary>The text the brick list is narrowed to, as typed in the search box; registry-only bricks match too.</summary>
    public string Search { get; set; } = "";

    /// <summary>
    /// What one frame draws, read once in the build pass. Actions finish on background threads; reading the controller
    /// again in the render pass could draw a node the build pass never laid out, at the window's corner.
    /// </summary>
    sealed record Frame(bool HasWorkspace, BricksTab Tab, BrickScope Scope, PackageScope? WorkspaceScope, bool Busy,
        bool RestartRequired, IReadOnlyList<CatalogBrick> Catalog, IReadOnlyList<BrickChange> Pending,
        IReadOnlyList<ScopedRegistry> Registries, string? PublicRegistry)
    {
        public static Frame Of(BricksController controller) => new(controller.HasWorkspace, controller.Tab, controller.Scope,
            controller.Workspace?.Scope, controller.IsBusy, controller.StudioRestartRequired,
            controller.Catalog, controller.Pending, controller.Registries, controller.Workspace?.PublicRegistry?.Name);
    }

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

        // Until the user picks a scope, follow the project: a panel first drawn before a project opened must not stay
        // on the studio's bricks once one is open, or the project's registries and broken bricks are silently hidden.
        var first = !loaded;
        loaded = true;
        var followed = !scopeChosen && FollowProject();
        if (first && !followed)
        {
            controller.Refresh();
        }

        if (first || followed)
        {
            _ = controller.RefreshRegistriesAsync();
        }

        pointer = gui.Input.MousePosition;
        if (controller.Revision != seenRevision)
        {
            seenRevision = controller.Revision;
            inspector?.Refresh(controller);
        }

        if (!listening)
        {
            listening = true;
            controller.Problem += problems.Enqueue;
        }

        if (gui.Pass == Pass.Pass1Build || frame is null)
        {
            frame = Frame.Of(controller);
            ShowProblems(gui);
        }

        Draw(gui, frame);
        gui.CascadeMenu(ref addMenuOpen, menuAt, BuildAddMenu);
        gui.CascadeMenu(ref moreMenuOpen, menuAt, BuildMoreMenu);
        gui.CascadeMenu(ref categoryMenuOpen, menuAt, BuildCategoryMenu);
    }

    void Draw(Gui gui, Frame now)
    {
        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(Theme.Gap).Padding(10f, 8f).Enter())
        {
            Header(gui, now);
            if (!now.HasWorkspace)
            {
                Line(gui, now.Scope == BrickScope.Project
                    ? "Open a project to manage its bricks."
                    : "This application has no studio bricks.", Theme.InkDim);
                return;
            }

            Notice(gui, now);
            if (now.Tab == BricksTab.Registries)
            {
                Registries(gui, now);
                return;
            }

            Toolbar(gui, now);
            if (addLabel is not null)
            {
                AddRow(gui, now);
            }

            CleanupRow(gui, now);
            PendingBar(gui, now);
            List(gui, now);
        }
    }

    void Show(Action select)
    {
        select();
        inspector?.ShowSelection(controller);
    }

    /// <summary>
    /// One row: the Bricks and Registries tabs, and, at the right, the Studio and Project filter that chooses whose
    /// bricks both show, when the host has both.
    /// </summary>
    void Header(Gui gui, Frame now)
    {
        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/header").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            if (now.HasWorkspace)
            {
                foreach (var (tab, label, width) in new[] { (BricksTab.Bricks, "Bricks", 56f), (BricksTab.Registries, "Registries", 76f) })
                {
                    if (Chip(gui, label, $"bricks/tab/{tab}", width, now.Tab == tab) && controller.Tab != tab)
                    {
                        Show(() => controller.Tab = tab);
                    }
                }
            }

            using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/header/gap").ExpandWidth().Enter())
            {
            }

            if (controller.HasStudio)
            {
                Scopes(gui, now);
            }
        }
    }

    // Shows the open project's bricks, or the studio's when no project is open; returns whether the scope changed.
    bool FollowProject()
    {
        var scope = controller.HasProject || !controller.HasStudio ? BrickScope.Project : BrickScope.Studio;
        if (controller.Scope == scope)
        {
            return false;
        }

        controller.Scope = scope;
        inspector?.ShowSelection(controller);
        return true;
    }

    void Scopes(Gui gui, Frame now)
    {
        foreach (var (scope, label, width) in new[] { (BrickScope.Studio, "Studio", 56f), (BrickScope.Project, "Project", 60f) })
        {
            if (!Chip(gui, label, $"bricks/scope/{scope}", width, now.Scope == scope) || controller.Scope == scope)
            {
                continue;
            }

            scopeChosen = true;
            controller.Scope = scope;
            _ = controller.RefreshRegistriesAsync();
            inspector?.ShowSelection(controller);
        }
    }

    void Registries(Gui gui, Frame now)
    {
        var height = Theme.Scale(rowHeight);

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight + 4f), "bricks/registries/toolbar").ExpandWidth()
                   .Direction(Axis.Horizontal).Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            if (Button(gui, now, "+", "bricks/registries/add", 28f, "Add a registry to take bricks from."))
            {
                Show(() => controller.SelectedRegistry = "");
            }
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
        {
            gui.ScrollY();
            foreach (var registry in now.Registries)
            {
                RegistryRow(gui, now, registry, height);
            }
        }
    }

    void RegistryRow(Gui gui, Frame now, ScopedRegistry registry, float height)
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

            Cell(gui, $"{id}/scopes", registry.Scopes.Count == 0 ? "*" : string.Join(", ", registry.Scopes), 110f, height);

            // The built-in registry is every workspace's own; any other is the user's to stop using.
            if (registry.Name != now.PublicRegistry
                && Button(gui, now, "Remove", $"{id}/remove", 60f, "Stop taking bricks from this registry."))
            {
                _ = controller.RemoveRegistryAsync(registry.Name);
            }
        }
    }

    void Toolbar(Gui gui, Frame now)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);

        using (gui.Node(-1, height, "bricks/toolbar").ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Gap)
                   .ContentAlignY(0.5f).Enter())
        {
            if (Button(gui, now, "+", "bricks/add", 28f, "Add a brick by name, from a git repository, a folder or a .brick file."))
            {
                OpenMenu(out addMenuOpen);
            }

            using (gui.Node(-1, height, "bricks/search").Expand().Enter())
            {
                Search = gui.TextInput(Search, width: 0, height: height, placeholder: "Search bricks",
                    fontSize: Theme.Text(12), padding: 5, id: "bricks/search/input");
            }

            if (Button(gui, now, "…", "bricks/more", 28f,
                    "Refresh the registries, restore the project's bricks, update them all or clean up old downloads."))
            {
                OpenMenu(out moreMenuOpen);
            }
        }

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/filters").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).ContentAlignY(0.5f).Enter())
        {
            foreach (var (value, label, width) in Filters)
            {
                if (Chip(gui, label, $"bricks/filter/{value}", width, filter == value))
                {
                    filter = value;
                }
            }

            using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/filters/gap").ExpandWidth().Enter())
            {
            }

            var (_, category, categoryWidth) = Categories.First(c => c.Category == Category);
            if (DropdownChip(gui, category, "bricks/category", categoryWidth + 16f, Category != BrickCategoryFilter.All))
            {
                OpenMenu(out categoryMenuOpen);
            }
        }
    }

    void BuildCategoryMenu(FlyoutBuilder menu)
    {
        foreach (var (value, label, _) in Categories)
        {
            menu.Item(label, () => Category = value);
        }
    }

    void OpenMenu(out bool open)
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
        menu.Separator();
        menu.Item("Clean up downloads…", OpenCleanup);
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
            if (path is not null)
            {
                _ = controller.AddFromSourceAsync($"file:{path}");
            }
        });

    void AddRow(Gui gui, Frame now)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);

        using (gui.Node(-1, height, "bricks/addRow").ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Gap).Enter())
        {
            using (gui.Node(-1, height, "bricks/addRow/input").Expand().Enter())
            {
                addInput = gui.TextInput(addInput, width: 0, height: height, placeholder: addLabel ?? "",
                    fontSize: Theme.Text(12), padding: 5, id: "bricks/addRow/text");
            }

            if (Button(gui, now, "Add", "bricks/addRow/add", 50f) && addInput.Trim().Length > 0)
            {
                _ = addAction?.Invoke(addInput);
                addLabel = null;
            }

            if (Button(gui, now, "Cancel", "bricks/addRow/cancel", 60f))
            {
                addLabel = null;
            }
        }
    }

    void Notice(Gui gui, Frame now)
    {
        if (now.RestartRequired)
        {
            Line(gui, "Restart Studio to activate the changed plugin assemblies, menus and panels.", Theme.Ink);
        }

        if (now.Busy)
        {
            Line(gui, "Working…", Theme.InkDim);
        }
    }

    /// <summary>Problems are toasts, not lines in the panel: the first line of each, the log holds the rest.</summary>
    void ShowProblems(Gui gui)
    {
        while (problems.TryDequeue(out var message))
        {
            gui.Toast(ToastText(message), ProblemToast);
        }
    }

    /// <summary>One line a toast can show: the message's first line, shortened, and how many more lines there were.</summary>
    /// <param name="message">The problem, possibly several lines.</param>
    /// <returns>The text to toast.</returns>
    public static string ToastText(string message)
    {
        var lines = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
        var first = lines.Count > 0 ? lines[0] : message.Trim();
        if (first.Length > 110)
        {
            first = $"{first[..107]}...";
        }

        return lines.Count > 1 ? $"{first} (+{lines.Count - 1} more, see the log)" : first;
    }
}
