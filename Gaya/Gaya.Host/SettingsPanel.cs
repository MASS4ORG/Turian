namespace Gaya.Host;

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
/// <param name="settings">The registered settings pages and their storage.</param>
/// <param name="log">Receives file and form failures.</param>
/// <param name="localization">Translates the panel's strings; null leaves them in English.</param>
/// <param name="themes">The themes available through the appearance dropdown.</param>
/// <param name="browseThemes">Opens a list of installable themes; without it the Browse button is hidden.</param>
sealed class SettingsPanel(IEditorSettings settings, ILogger log, IShellLocalization? localization,
    IThemeService themes, Action? browseThemes = null) : IPanel
{
    const float categoryWidth = 210f;
    const float editorWidth = 280f;
    const float markerWidth = 3f;

    readonly TreeViewState categories = new();
    readonly Dictionary<string, (object Target, FormModel Model)> forms = [];

    string filter = "";
    string? selectedId;
    string? frameSelectedId;
    SettingsScope scope = SettingsScope.User;
    float contentWidth = 360f;
    float measuredWidth;
    string? frameThemeSelection;

    static ThemeTokens Theme => ThemeTokens.Current;

    FormRenderContext? formContext;

    /// <summary>The panel's form context, translating enum labels into the studio's language.</summary>
    FormRenderContext FormContext => formContext ??= new FormRenderContext { Translate = T };

    string T(string source) => localization?.T(source) ?? source;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        // Descriptions wrap to the page's width, which is only resolved in the render pass. Both
        // passes have to agree on it, so what this frame wraps to is what the last one measured.
        if (gui.Pass == Pass.Pass1Build) contentWidth = measuredWidth;

        var pages = Visible();
        if (selectedId is null || pages.All(page => !SameCategory(page, selectedId)))
            Select(pages.FirstOrDefault() is { } first ? TopLevel(first) : null);
        if (gui.Pass == Pass.Pass1Build) frameSelectedId = selectedId;

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
                    Category(gui, pages);
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

            ScopeTab(gui, SettingsScope.User, T("User"), height);
            ScopeTab(gui, SettingsScope.Workspace, T("Workspace"), height);
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

        filter = gui.TextInput(filter, width: 0, height: rowHeight, placeholder: T("Search settings"),
            fontSize: Theme.Text(12), padding: 5, id: "settings/filter");

        using (gui.Node(-1, rowHeight, "settings/openJson").ExpandWidth().ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(Theme.Hover, 3f);
            gui.DrawText(T("Open settings.json"), Theme.Text(11f), hot ? Theme.Ink : Theme.InkDim,
                centerInRect: false);

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick()) OpenJson();
        }

        gui.TreeView(categories, Rows(pages), SettingsStyle.Tree(), OnCategoryClick);
    }

    /// <summary>Lists each top-level category once; nested sections are rendered with their options.</summary>
    IReadOnlyList<TreeItem> Rows(IReadOnlyList<SettingsPageDescriptor> pages) =>
        [.. pages.Select(TopLevel).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(category => new TreeItem(category, T(category), 0, Tag: category))];

    static string TopLevel(SettingsPageDescriptor page) => Segments(page.Path)[0];

    static bool SameCategory(SettingsPageDescriptor page, string? category) =>
        string.Equals(TopLevel(page), category, StringComparison.OrdinalIgnoreCase);

    void OnCategoryClick(TreeViewEvent clicked)
    {
        if (clicked.Item.Tag is string category) Select(category);
    }

    /// <summary>Selects a category, keeping the left-hand highlight on the same row.</summary>
    void Select(string? category)
    {
        selectedId = category;
        categories.SelectedId = category;
    }

    /// <summary>Shows the category's own options and all nested sections in a single scrolling panel.</summary>
    void Category(Gui gui, IReadOnlyList<SettingsPageDescriptor> pages)
    {
        if (gui.Pass == Pass.Pass2Render)
            measuredWidth = Math.Max(120f, gui.CurrentNode.Rect.W - Theme.Scale(40f));

        if (frameSelectedId is null)
        {
            gui.DrawText(EmptyMessage(), Theme.Text(12), Theme.InkDim, wrapWidth: contentWidth,
                centerInRect: false);
            return;
        }

        Heading(gui, frameSelectedId, frameSelectedId, 0);
        var headings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages.Where(page => SameCategory(page, frameSelectedId))
                     .OrderBy(page => page.Path, StringComparer.OrdinalIgnoreCase))
        {
            var segments = Segments(page.Path);
            for (var depth = 1; depth < segments.Length; depth++)
            {
                var path = string.Join('/', segments[..(depth + 1)]);
                if (headings.Add(path)) Heading(gui, path, segments[depth], depth);
            }
            Page(gui, page);
        }
    }

    void Heading(Gui gui, string path, string title, int depth)
    {
        using (gui.Node(-1, Theme.Scale(depth == 0 ? 32f : 28f), "settings/heading/" + path)
                   .ExpandWidth().Margin(0, depth == 0 ? 0 : 8f).ContentAlignY(0.5f).Enter())
            gui.DrawText(T(title), Theme.Text(Math.Max(13f, 20f - depth * 3f)), Theme.Ink, centerInRect: false);
    }

    /// <summary>Renders a contributed page's description and options beneath its section heading.</summary>
    void Page(Gui gui, SettingsPageDescriptor page)
    {
        if (page.Description.Length > 0)
            gui.DrawText(T(page.Description), Theme.Text(12), Theme.InkDim, wrapWidth: contentWidth,
                centerInRect: false);

        var fields = Form(page).Sections.SelectMany(section => section.BodyFields).ToList();
        var shown = fields.Where(field => MatchesFilter(page, field)).ToList();

        for (var i = 0; i < shown.Count; i++) Option(gui, page, shown[i], $"settings/{page.Id}/field{i}");
    }

    /// <summary>Why the right-hand side is empty, which is not the same question in every scope.</summary>
    string EmptyMessage()
    {
        if (scope == SettingsScope.Workspace && !settings.HasWorkspace)
            return T("Open a project to edit the settings stored with it.");

        return T(filter.Length > 0 ? "No setting matches the filter." : "Nothing is registered in this scope.");
    }

    /// <summary>
    /// A title, the sentence under it, and the editor its type asks for. A value that differs from
    /// what the page declares is marked down its left edge and offered a way back.
    /// </summary>
    void Option(Gui gui, SettingsPageDescriptor page, FormField field, string id)
    {
        var modified = IsModified(page, field);

        using (gui.Node(-1, -1, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).Margin(0, 6f).Enter())
        {
            using (gui.Node(markerWidth, -1, $"{id}/marker").ExpandHeight().Enter())
                if (gui.Pass == Pass.Pass2Render && modified)
                    gui.DrawBackgroundRect(Theme.Accent, 1.5f);

            using (gui.Node().Expand().Direction(Axis.Vertical).Gap(3f).Enter())
                OptionContent(gui, page, field, id, modified);
        }
    }

    void OptionContent(Gui gui, SettingsPageDescriptor page, FormField field, string id, bool modified)
    {
        var setting = field.Attribute<EditorSettingAttribute>();
        var title = setting is { Path.Length: > 0 } ? setting.Path : field.Label;
        var description = setting?.Description ?? "";
        OptionHeading(gui, page, field, id, title, modified);

        if (description.Length > 0)
            gui.DrawText(T(description), Theme.Text(11f), Theme.InkDim, wrapWidth: contentWidth,
                centerInRect: false);

        using (gui.Node(Theme.Scale(editorWidth), Theme.Scale(Theme.RowHeight), $"{id}/editor")
                   .Direction(Axis.Horizontal).Gap(4f).Enter())
        {
            SettingsStyle.ApplyFormStyle(gui);
            FieldEditor(gui, field, id);
        }
    }

    void OptionHeading(Gui gui, SettingsPageDescriptor page, FormField field, string id, string title, bool modified)
    {
        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), $"{id}/title").ExpandWidth()
                   .Direction(Axis.Horizontal).Gap(6f).ContentAlignY(0.5f).Enter())
        {
            gui.DrawText(T(title), Theme.Text(13f), Theme.Ink, centerInRect: false);
            if (modified && RevertButton(gui, $"{id}/revert")) Revert(page, field);
            using (gui.Node().Expand().Enter()) { }
        }
    }

    void FieldEditor(Gui gui, FormField field, string id)
    {
        if (field.Target is AppearanceSettings && field.Name == nameof(AppearanceSettings.Theme))
            ThemeEditor(gui, field, id);
        else
            gui.FormFieldEditor(field, id, FormContext);
    }

    void ThemeEditor(Gui gui, FormField field, string id)
    {
        var available = themes.ColorThemes;
        var names = available.Select(theme => theme.Name).ToArray();
        var current = available.ToList().FindIndex(theme => string.Equals(theme.Id, themes.CommittedColorTheme,
            StringComparison.OrdinalIgnoreCase));
        var style = gui.ControlStyle;
        // ReSharper disable once ExplicitCallerInfoArgument
        var next = gui.Dropdown(names, current, width: 0, height: Theme.Scale(Theme.RowHeight), fontSize: Theme.Text(12),
            backgroundColor: style.Surface, borderColor: style.Border, textColor: style.Text,
            dropdownColor: style.Popup, filePath: $"{id}/theme");
        if (gui.Pass == Pass.Pass1Build)
            frameThemeSelection = next >= 0 && next != current ? available[next].Id : null;
        else if (frameThemeSelection is { } selected)
            field.SetValue(selected);

        if (browseThemes is not null && BrowseButton(gui, $"{id}/browse")) browseThemes();
    }

    /// <summary>The button beside the theme dropdown that lists installable themes.</summary>
    bool BrowseButton(Gui gui, string id)
    {
        using (gui.Node(Theme.Scale(76f), Theme.Scale(Theme.RowHeight), id).BlockInput()
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(hot ? Theme.Hover : Theme.Chrome, 3f);
            gui.DrawText(T("Browse…"), Theme.Text(11f), hot ? Theme.Ink : Theme.InkDim);
            gui.Tooltip(gui.CurrentNode, T("Find themes to install as bricks."), maxWidth: 320);

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
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

            gui.DrawText(SettingsStyle.RevertIcon, Theme.Text(13), hot ? Theme.Ink : Theme.InkDim);

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
        var model = FormBuilder.Build(page.Target, new FormOptions
        {
            MutationNotifier = _ => settings.NotifyChanged(pageId),
            FailureReporter = ReportFailure,
        });
        forms[page.Id] = (page.Target, model);
        return model;
    }

    /// <summary>A failing action is an error the user should see in the log; a failing getter or setter is noise.</summary>
    void ReportFailure(FormFailure failure)
    {
        if (failure.Kind == FormFailureKind.Action)
            log.LogError(failure.Exception, "Settings: {Member} failed on {Target}", failure.Member,
                failure.Target.GetType().Name);
        else
            log.LogDebug(failure.Exception, "Settings: {Kind} of {Member} failed on {Target}", failure.Kind,
                failure.Member, failure.Target.GetType().Name);
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
