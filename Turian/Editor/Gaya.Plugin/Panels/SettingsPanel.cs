namespace Gaya.Plugin.Turian;

/// <summary>
/// Every editor setting in one place, laid out the way an IDE does it: the scope tabs and the
/// categories on the left under a filter box, the chosen page's options on the right. The pages come
/// from <see cref="IEditorSettings"/> — built-in ones and whatever user code contributes — and each
/// page is drawn by reflecting over its object with <see cref="FormBuilder"/>, so contributing
/// settings costs nothing but a class.
/// </summary>
/// <remarks>
/// Edits are never staged: writing a field goes straight to the live object and the file follows a
/// moment later, which is what the rest of the settings-owning programs a user knows do.
/// </remarks>
sealed class SettingsPanel(IEditorSettings settings, ILogger log, StudioLocalization localization) : IPanel
{
    const float categoryWidth = 210f;
    const float editorWidth = 280f;
    const float markerWidth = 3f;

    readonly TreeViewState categories = new();
    readonly Dictionary<string, (object Target, FormModel Model)> forms = [];

    string filter = "";
    string? selectedId;
    SettingsScope scope = SettingsScope.User;
    float contentWidth = 360f;
    float measuredWidth;

    static StudioTheme Theme => StudioTheme.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        // Descriptions wrap to the page's width, which is only resolved in the render pass. Both
        // passes have to agree on it, so what this frame wraps to is what the last one measured.
        if (gui.Pass == Pass.Pass1Build) contentWidth = measuredWidth;

        var pages = Visible();
        if (selectedId is null || pages.All(page => page.Id != selectedId))
            Select(pages.FirstOrDefault()?.Id);

        using (gui.Node().Expand().Direction(Axis.Vertical).Enter())
        {
            ScopeTabs(gui);

            using (gui.Node().Expand().Direction(Axis.Horizontal).Gap(Theme.Gap).Enter())
            {
                using (gui.Node(Theme.Scale(categoryWidth)).ExpandHeight().Direction(Axis.Vertical)
                           .Gap(4f).Padding(6f).Enter())
                    Categories(gui, pages);

                using (gui.Node().Expand().Direction(Axis.Vertical).Gap(Theme.Scale(8f))
                           .Padding(Theme.Scale(16f), Theme.Scale(12f)).Enter())
                {
                    gui.ScrollY();
                    Page(gui, pages.FirstOrDefault(page => page.Id == selectedId));
                }
            }
        }
    }

    /// <summary>
    /// The two scopes as tabs: what the user carries between projects, and what belongs to the open
    /// one. The active tab is underlined rather than filled, the way an IDE marks it.
    /// </summary>
    void ScopeTabs(Gui gui)
    {
        var height = Theme.Scale(Theme.HeaderHeight + 4f);

        using (gui.Node(-1, height, "settings/scopes").ExpandWidth().Direction(Axis.Horizontal).Enter())
        {
            gui.DrawBackgroundRect(Theme.Chrome);

            ScopeTab(gui, SettingsScope.User, localization.T("User"), height);
            ScopeTab(gui, SettingsScope.Workspace, localization.T("Workspace"), height);
        }
    }

    void ScopeTab(Gui gui, SettingsScope target, string label, float height)
    {
        var selected = scope == target;

        using (gui.Node(Theme.Scale(96f), height, $"settings/scope/{target}")
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
            {
                if (hot && !selected) gui.DrawBackgroundRect(Theme.Hover);

                if (selected)
                {
                    var rect = gui.CurrentNode.Rect;
                    gui.DrawRect(new Rect(rect.X, rect.Y + rect.H - 2f, rect.W, 2f), Theme.Accent);
                }

                if (hot && interactable.OnClick() && !selected)
                {
                    scope = target;
                    Select(null);
                }
            }

            gui.DrawText(label, Theme.Text(12f), selected ? Theme.Ink : Theme.InkDim);
        }
    }

    /// <summary>The filter box, the file this scope is stored in, and the category tree under them.</summary>
    void Categories(Gui gui, IReadOnlyList<SettingsPageDescriptor> pages)
    {
        var rowHeight = Theme.Scale(Theme.RowHeight + 4f);

        filter = gui.TextInput(filter, width: 0, height: rowHeight, placeholder: localization.T("Search settings"),
            fontSize: Theme.Text(12), padding: 5, id: "settings/filter");

        using (gui.Node(-1, rowHeight, "settings/openJson").ExpandWidth().ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(Theme.Hover, 3f);
            gui.DrawText(localization.T("Open settings.json"), Theme.Text(11f), hot ? Theme.Ink : Theme.InkDim,
                centerInRect: false);

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick()) OpenJson();
        }

        gui.TreeView(categories, Rows(pages), StudioControls.Tree(), OnCategoryClick);
    }

    /// <summary>
    /// The pages as a tree: a page whose path has several segments hangs under a row for each leading
    /// one, so <c>Editor/Camera</c> and <c>Editor/Grid</c> share an <c>Editor</c> parent.
    /// </summary>
    IReadOnlyList<TreeItem> Rows(IReadOnlyList<SettingsPageDescriptor> pages)
    {
        var rows = new List<TreeItem>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
        {
            var segments = Segments(page.Path);

            for (var depth = 0; depth < segments.Length - 1; depth++)
            {
                var groupId = string.Join('/', segments[..(depth + 1)]);
                if (emitted.Add(groupId))
                    rows.Add(new TreeItem(groupId, localization.T(segments[depth]), depth, HasChildren: true));
            }

            rows.Add(new TreeItem(page.Id, localization.T(segments[^1]), segments.Length - 1, Tag: page));
        }

        return rows;
    }

    void OnCategoryClick(TreeViewEvent clicked)
    {
        if (clicked.Item.Tag is SettingsPageDescriptor page) Select(page.Id);
    }

    /// <summary>Selects a page, keeping the tree's own highlight on the same row.</summary>
    void Select(string? pageId)
    {
        selectedId = pageId;
        categories.SelectedId = pageId;
    }

    /// <summary>One page: its title and description, then every option that survives the filter.</summary>
    void Page(Gui gui, SettingsPageDescriptor? page)
    {
        if (gui.Pass == Pass.Pass2Render)
            measuredWidth = Math.Max(120f, gui.CurrentNode.Rect.W - Theme.Scale(40f));

        if (page is null)
        {
            gui.DrawText(EmptyMessage(), Theme.Text(12), Theme.InkDim, wrapWidth: contentWidth,
                centerInRect: false);
            return;
        }

        gui.DrawText(localization.T(page.Title), Theme.Text(20f), Theme.Ink, centerInRect: false);

        if (page.Description.Length > 0)
            gui.DrawText(localization.T(page.Description), Theme.Text(12), Theme.InkDim, wrapWidth: contentWidth,
                centerInRect: false);

        var fields = Form(page).Sections.SelectMany(section => section.BodyFields).ToList();
        var shown = fields.Where(field => MatchesFilter(page, field)).ToList();

        for (var i = 0; i < shown.Count; i++) Option(gui, page, shown[i], $"settings/{page.Id}/field{i}");

        if (shown.Count == 0)
            gui.DrawText(localization.T("This page has no editable options."), Theme.Text(12), Theme.InkDim,
                centerInRect: false);
    }

    /// <summary>Why the right-hand side is empty, which is not the same question in every scope.</summary>
    string EmptyMessage()
    {
        if (scope == SettingsScope.Workspace && !settings.HasWorkspace)
            return localization.T("Open a project to edit the settings stored with it.");

        return localization.T(filter.Length > 0 ? "No setting matches the filter." : "Nothing is registered in this scope.");
    }

    /// <summary>
    /// A title, the sentence under it, and the editor its type asks for. A value that differs from
    /// what the page declares is marked down its left edge and offered a way back.
    /// </summary>
    void Option(Gui gui, SettingsPageDescriptor page, FormField field, string id)
    {
        var setting = field.Attribute<EditorSettingAttribute>();
        var title = setting is { Path.Length: > 0 } ? setting.Path : field.Label;
        var description = setting?.Description ?? "";
        var modified = IsModified(page, field);

        using (gui.Node(-1, -1, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).Margin(0, 6f).Enter())
        {
            using (gui.Node(markerWidth, -1, $"{id}/marker").ExpandHeight().Enter())
                if (gui.Pass == Pass.Pass2Render && modified)
                    gui.DrawBackgroundRect(Theme.Accent, 1.5f);

            using (gui.Node().Expand().Direction(Axis.Vertical).Gap(3f).Enter())
            {
                using (gui.Node(-1, Theme.Scale(Theme.RowHeight), $"{id}/title").ExpandWidth()
                           .Direction(Axis.Horizontal).Gap(6f).ContentAlignY(0.5f).Enter())
                {
                    gui.DrawText(localization.T(title), Theme.Text(13f), Theme.Ink, centerInRect: false);

                    // Beside the title rather than against the panel's edge: at a settings page's
                    // width the two would otherwise be too far apart to read as one row.
                    if (modified && RevertButton(gui, $"{id}/revert")) Revert(page, field);

                    using (gui.Node().Expand().Enter()) { }
                }

                if (description.Length > 0)
                    gui.DrawText(localization.T(description), Theme.Text(11f), Theme.InkDim, wrapWidth: contentWidth,
                        centerInRect: false);

                using (gui.Node(Theme.Scale(editorWidth), Theme.Scale(Theme.RowHeight), $"{id}/editor")
                           .Direction(Axis.Horizontal).Gap(4f).Enter())
                    FieldDrawers.DrawEditorOnly(gui, field, id, localization.T);
            }
        }
    }

    /// <summary>The button that puts a setting back the way the page declares it.</summary>
    static bool RevertButton(Gui gui, string id)
    {
        var size = Theme.Scale(Theme.RowHeight);

        using (gui.Node(size, size, id).BlockInput().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(Theme.Hover, 3f);

            gui.DrawText("↺", Theme.Text(13), hot ? Theme.Ink : Theme.InkDim);

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }

    /// <summary>Whether a value differs from what its page declares.</summary>
    static bool IsModified(SettingsPageDescriptor page, FormField field) =>
        page.Defaults.TryGetValue(field.Name, out var original) && !Equals(field.GetValue(), original);

    void Revert(SettingsPageDescriptor page, FormField field)
    {
        if (!page.Defaults.TryGetValue(field.Name, out var original)) return;

        field.SetValue(original);
        settings.NotifyChanged(page.Id);
    }

    /// <summary>
    /// The form for a page, rebuilt when the page's object is replaced — which is what a recompile
    /// does to every user-code page at once.
    /// </summary>
    FormModel Form(SettingsPageDescriptor page)
    {
        if (forms.TryGetValue(page.Id, out var cached) && ReferenceEquals(cached.Target, page.Target))
            return cached.Model;

        var pageId = page.Id;
        var model = FormBuilder.Build(page.Target, _ => settings.NotifyChanged(pageId));
        forms[page.Id] = (page.Target, model);
        return model;
    }

    /// <summary>
    /// The pages of the chosen scope that the filter leaves. A page matches on its own path, and also
    /// on any option whose title or description mentions the text — searching for "speed" has to find
    /// the page holding it.
    /// </summary>
    IReadOnlyList<SettingsPageDescriptor> Visible()
    {
        var inScope = settings.Pages.Where(page => !page.Hidden).Where(page => page.Scope == scope).ToList();
        if (filter.Length == 0) return inScope;

        return [.. inScope.Where(page => Contains(page.Path) || Contains(page.Description)
            || Form(page).Sections.SelectMany(section => section.BodyFields).Any(Matches))];
    }

    /// <summary>Whether one option survives the filter: a page matched by name shows all of them.</summary>
    bool MatchesFilter(SettingsPageDescriptor page, FormField field) =>
        filter.Length == 0 || Contains(page.Path) || Matches(field);

    bool Matches(FormField field)
    {
        var setting = field.Attribute<EditorSettingAttribute>();
        return Contains(field.Label) || Contains(setting?.Path ?? "") || Contains(setting?.Description ?? "");
    }

    bool Contains(string text) => text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hands the scope's file to whatever the desktop opens JSON with. Everything is written first,
    /// because a scope that has never been edited has no file yet.
    /// </summary>
    void OpenJson()
    {
        settings.Save();

        var path = settings.PathFor(scope);
        if (!File.Exists(path))
        {
            log.LogWarning("Settings: {Scope} scope has no file to open", scope);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogWarning(ex, "Settings: {Path} could not be opened", path);
        }
    }

    static string[] Segments(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 0 ? segments : ["Settings"];
    }
}
