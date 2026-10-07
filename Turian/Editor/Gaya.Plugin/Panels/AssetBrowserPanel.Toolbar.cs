namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    const string searchId = "assets/search";
    string search = "";
    IReadOnlyList<string> filterTypes = [];
    IReadOnlyList<string> filterLabels = [];
    bool favoritesOnly;
    bool searchFocusRequested;
    AssetFilter frameFilter = new();
    IReadOnlyList<BreadcrumbItem> breadcrumbs = [];
    IReadOnlyList<string> typeOptions = [];
    IReadOnlyList<string> labelOptions = [];
    float toolbarWidth = 800;
    bool compactToolbar;
    bool toolbarDataDirty = true;
    bool breadcrumbsDirty = true;

    void Toolbar(Gui gui)
    {
        PrepareToolbar(gui);
        NavigationToolbar(gui);
        ToolsToolbar(gui);
        CompactToolbar(gui);
        SnapshotFilters(gui);
    }

    void PrepareToolbar(Gui gui)
    {
        var theme = ThemeTokens.Current;
        if (gui.Pass == Pass.Pass1Build)
        {
            compactToolbar = toolbarWidth < theme.Scale(650);
            if (breadcrumbsDirty) { breadcrumbs = BuildBreadcrumbs(); breadcrumbsDirty = false; }
            if (toolbarDataDirty)
            {
                typeOptions = [AssetQuery.FolderType, .. types.Types.Select(type => type.Id), AssetQuery.OtherType];
                labelOptions = AssetLabelService.Collect(entries);
                toolbarDataDirty = false;
            }
        }
    }

    void NavigationToolbar(Gui gui)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(-1, theme.Scale(24), "assets/navigation").ExpandWidth().Direction(Axis.Horizontal)
                   .ContentAlignY(0.5f).Gap(2).Enter())
        {
            if (gui.Pass == Pass.Pass2Render) toolbarWidth = gui.CurrentNode.Rect.W;
            NavButton(gui, "←", "back", navigation.CanBack, () => { navigation.Back(); NavigationChanged(); });
            NavButton(gui, "→", "forward", navigation.CanForward, () => { navigation.Forward(); NavigationChanged(); });
            var parent = navigation.Current is { } current ? ParentOf(current) : null;
            NavButton(gui, "↑", "up", parent is not null, () => NavigateTo(parent!));
            using (gui.Node().Expand().Enter())
            {
                gui.ClipContent();
                gui.Breadcrumb(breadcrumbs, height: theme.Scale(24), fontSize: theme.Text(11));
            }
        }
    }

    void ToolsToolbar(Gui gui)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(-1, theme.Scale(26), "assets/tools").ExpandWidth().Direction(Axis.Horizontal)
                   .ContentAlignY(0.5f).Gap(4).Enter())
        {
            SearchControl(gui);
            if (StudioControls.SmallTextButton(gui, "×", "assets/clearFilters", theme.Scale(22), "Clear filters")) ClearFilters();
            if (!compactToolbar) FilterControls(gui);
            ViewControls(gui);
            if (!compactToolbar) ZoomControl(gui);
        }
    }

    void CompactToolbar(Gui gui)
    {
        var theme = ThemeTokens.Current;
        if (compactToolbar)
        {
            using (gui.Node(-1, theme.Scale(26), "assets/filters").ExpandWidth().Direction(Axis.Horizontal)
                       .ContentAlignY(0.5f).Gap(4).Enter())
            {
                FilterControls(gui);
                ZoomControl(gui);
            }
        }
    }

    void SnapshotFilters(Gui gui)
    {
        if (gui.Pass == Pass.Pass1Build && queryDirty)
            frameFilter = new AssetFilter
            {
                Name = search.Trim(),
                Types = filterTypes.ToHashSet(),
                Labels = filterLabels.ToHashSet(),
                FavoritesOnly = favoritesOnly,
            };
    }

    void SearchControl(Gui gui)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(0, theme.Scale(24), "assets/searchField").ExpandWidth().Enter())
        {
            var next = gui.TextInput(search, width: 0, height: theme.Scale(24), placeholder: "Search assets",
                fontSize: theme.Text(11), id: searchId, grabFocus: searchFocusRequested);
            if (next != search) { search = next; QueryChanged(); }
            if (gui.Pass == Pass.Pass2Render && searchFocusRequested)
            {
                searchFocusRequested = false;
            }
            if (gui.Pass == Pass.Pass2Render && gui.HasFocusWithin("assets/searchField") && gui.Input.IsKeyPressed(KeyboardKey.Escape))
            {
                ClearFilters();
                gui.ClearFocus();
            }
        }
    }

    void ViewControls(Gui gui)
    {
        var theme = ThemeTokens.Current;
        var value = favoritesOnly;
        gui.Checkbox(ref value, "Fav", size: theme.Scale(13), fontSize: theme.Text(11), spacing: 3);
        if (value != favoritesOnly) { favoritesOnly = value; QueryChanged(); }
        if (StudioControls.SmallTextButton(gui, browserSettings.ViewMode.ToString(), "assets/view", theme.Scale(42), "Cycle tree / split / grid"))
        {
            browserSettings.ViewMode = (AssetBrowserViewMode)(((int)browserSettings.ViewMode + 1) % 3);
            editorSettings.NotifyChanged(AssetBrowserSettings.PageId);
        }
    }

    void FilterControls(Gui gui)
    {
        var theme = ThemeTokens.Current;
        var kinds = gui.MultiDropdown(typeOptions, filterTypes, display: TypeName, width: theme.Scale(90),
            height: theme.Scale(24), fontSize: theme.Text(11), placeholder: "Type");
        if (kinds.Changed) { filterTypes = kinds.Selected; QueryChanged(); }
        var labels = gui.MultiDropdown(labelOptions, filterLabels, width: theme.Scale(90), height: theme.Scale(24),
            fontSize: theme.Text(11), placeholder: "Labels");
        if (labels.Changed) { filterLabels = labels.Selected; QueryChanged(); }
    }

    string TypeName(string id) => id switch
    {
        AssetQuery.FolderType => "Folder",
        AssetQuery.OtherType => "Other",
        _ => types.Types.FirstOrDefault(type => type.Id == id)?.DisplayName ?? id,
    };

    void ZoomControl(Gui gui)
    {
        var zoom = (float)Math.Clamp(browserSettings.GridZoom, 32, 256);
        gui.Slider(ref zoom, 32, 256, step: 16, width: ThemeTokens.Current.Scale(80),
            height: ThemeTokens.Current.Scale(24));
        if (gui.Pass == Pass.Pass2Render && (int)zoom != browserSettings.GridZoom) SetZoom((int)zoom);
    }

    void SetZoom(int zoom)
    {
        browserSettings.GridZoom = Math.Clamp(zoom, 32, 256);
        editorSettings.NotifyChanged(AssetBrowserSettings.PageId);
    }

    static void NavButton(Gui gui, string text, string id, bool enabled, Action action)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(theme.Scale(22), theme.Scale(22), "assets/" + id).BlockInput().AlignContent(0.5f, 0.5f).Enter())
        {
            gui.DrawText(text, theme.Text(12), enabled ? theme.Ink : theme.InkFaint);
            if (gui.Pass != Pass.Pass2Render || !enabled) return;
            gui.RegisterFocusable();
            if (gui.GetInteractable().OnClick()) action();
        }
    }

    IReadOnlyList<BreadcrumbItem> BuildBreadcrumbs() => navigation.Current is { } current
        ? [.. AssetPathSegments.Build(current, entries).Select(segment => new BreadcrumbItem(segment.Name,
            () => NavigateTo(segment.Path), IsCurrent: segment.Path == current,
            Children: [.. ChildrenOf(segment.Path).Where(entry => entry.IsDirectory)
                .Select(entry => new BreadcrumbItem(Path.GetFileName(entry.AbsolutePath), () => NavigateTo(entry.AbsolutePath)))]))]
        : [];

    void NavigateTo(string folder)
    {
        navigation.Visit(folder);
        NavigationChanged();
    }

    void NavigationChanged()
    {
        breadcrumbsDirty = true;
        queryDirty = true;
        gridReset = true;
        if (navigation.Current is not { } current) return;
        for (var path = current; path is not null; path = ParentOf(path)) state.SetExpanded(path, true);
    }

    void QueryChanged()
    {
        queryDirty = true;
        gridReset = true;
    }

    /// <summary>Clears all browser filters and returns to the current folder.</summary>
    public void ClearFilters()
    {
        search = "";
        filterTypes = [];
        filterLabels = [];
        favoritesOnly = false;
        QueryChanged();
    }

    /// <summary>Focuses the search box on the next render pass.</summary>
    public void FocusSearch() => searchFocusRequested = true;
}
