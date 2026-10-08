namespace Gaya.Plugin.Turian;

[EditorSetting("Studio/Recent Projects", Id = "gaya.turian.recentProjects", Hidden = true)]
sealed class RecentProjectsSettings
{
    List<string> paths = [];

    /// <summary>Recent project directories, always stored as absolute paths.</summary>
    public List<string> Paths
    {
        get => paths;
        set => paths = [.. value.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)];
    }

    public void Add(string projectDirectory)
    {
        var path = Path.GetFullPath(projectDirectory);
        Paths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        Paths.Insert(0, path);
        if (Paths.Count > 12) Paths.RemoveRange(12, Paths.Count - 12);
    }

    public bool Remove(string projectDirectory) =>
        Paths.RemoveAll(existing => string.Equals(existing, projectDirectory, StringComparison.OrdinalIgnoreCase)) > 0;
}

sealed class ProjectSwitcherChrome(
    RecentProjectsSettings recent,
    SettingsService settings,
    ProjectSession session,
    ICommandDispatcher commands,
    IEditorSettings editorSettings,
    UnsavedChangesGuard unsaved) : IChromeItem, IDisposable
{
    readonly Dictionary<string, SKImage?> icons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);
    ProjectRow[] frameProjects = [];
    sealed record ProjectRow(string Path, string Title, SKImage? Icon);

    bool open;
    Vector2 popupPosition;

    static ThemeTokens Theme => ThemeTokens.Current;
    static float RowHeight => Theme.Scale(26);
    static float MenuWidth => Theme.Scale(340);

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        RenderCurrent(gui);
        if (gui.Pass == Pass.Pass1Build && open)
            frameProjects = [.. recent.Paths.Where(Directory.Exists)
                .Select(path => new ProjectRow(path, TitleFor(path), IconFor(path)))];
        var close = false;
        gui.Popup(ref open, () => close = RenderMenu(gui), width: MenuWidth,
            height: (frameProjects.Length + 1) * RowHeight + Theme.Scale(22), position: popupPosition);
        if (close) open = false;
    }

    void RenderCurrent(Gui gui)
    {
        var project = settings.Settings;
        var label = CurrentLabel(project);
        using (gui.Node(-1, RowHeight, "project-switcher").Width(UnitValue.Fit)
                   .Direction(Axis.Horizontal).Padding(8, 0).Gap(6).ContentAlignY(0.5f).Enter())
        {
            var anchor = gui.CurrentNode.Rect;
            DrawCurrentIcon(gui, project);
            gui.DrawText(label, Theme.Text(12), Theme.Ink, centerInRect: false);
            gui.DrawText(EditorIcons.CaretDown, Theme.Text(9), Theme.InkDim);
            if (gui.Pass != Pass.Pass2Render) return;
            var interaction = gui.GetInteractable();
            if (interaction.OnHover()) gui.DrawBackgroundRect(Theme.Hover, 2);
            if (!interaction.OnClick()) return;
            popupPosition = new Vector2(Math.Clamp(anchor.X, 0, Math.Max(0, gui.ScreenRect.W - MenuWidth)),
                anchor.Y + anchor.H);
            open = !open;
            if (project is not null) titles.Remove(project.ProjectAbsoluteDir);
        }
    }

    string CurrentLabel(AppSettings? project)
    {
        if (project is null) return "No project";
        var label = ProjectPresentation.Name(project);
        return unsaved.HasUnsavedChanges ? $"{label} \u25CF" : label;
    }

    void DrawCurrentIcon(Gui gui, AppSettings? project)
    {
        if (project is not null && IconFor(project.ProjectAbsoluteDir) is { } icon)
            gui.Image(icon, RowHeight, RowHeight);
    }

    string TitleFor(string path)
    {
        if (settings.Settings is { } current && string.Equals(current.ProjectAbsoluteDir, path,
                StringComparison.OrdinalIgnoreCase)) return ProjectPresentation.Name(current);
        if (!titles.TryGetValue(path, out var title)) titles[path] = title = ProjectPresentation.Name(path);
        return title;
    }

    bool RenderMenu(Gui gui)
    {
        var close = false;
        using (gui.Node().Expand().Direction(Axis.Vertical).Enter())
        {
            foreach (var project in frameProjects) close |= RenderProjectRow(gui, project);
            using (gui.Node(-1, Theme.Scale(6)).ExpandWidth().Enter())
                gui.DrawBackgroundRect(Theme.Border);
            using (gui.Node(-1, RowHeight, "project-switcher/open").ExpandWidth()
                       .Padding(8, 0).ContentAlignY(0.5f).Enter())
            {
                gui.DrawText("Open Project…", Theme.Text(12), Theme.Ink, centerInRect: false);
                if (ActivateMenuItem(gui))
                {
                    commands.Execute("gaya.turian.openProject");
                    close = true;
                }
            }
        }
        return close;
    }

    bool RenderProjectRow(Gui gui, ProjectRow project)
    {
        var close = false;
        using (gui.Node(-1, RowHeight, $"project-switcher/{project.Path}").ExpandWidth()
                   .Direction(Axis.Horizontal).Gap(4).Enter())
        {
            var row = gui.CurrentNode;
            using (gui.Node(-1, RowHeight, $"project-switcher/{project.Path}/open").ExpandWidth()
                       .Direction(Axis.Horizontal).Gap(6).Padding(8, 0).ContentAlignY(0.5f).Enter())
            {
                if (project.Icon is { } icon) gui.Image(icon, RowHeight, RowHeight);
                gui.DrawText(project.Title, Theme.Text(12), Theme.Ink, centerInRect: false);
                if (ActivateMenuItem(gui))
                {
                    SwitchProject(project.Path);
                    close = true;
                }
            }
            if (MenuAction(gui, EditorIcons.ArrowUpRightFromSquare, $"{row.Id}/new-window"))
            {
                OpenInNewInstance(project.Path);
                close = true;
            }
            if (MenuAction(gui, EditorIcons.Xmark, $"{row.Id}/remove"))
            {
                if (recent.Remove(project.Path)) editorSettings.NotifyChanged("gaya.turian.recentProjects");
                close = true;
            }
            gui.Tooltip(row, project.Path, maxWidth: 1000);
        }
        return close;
    }

    void SwitchProject(string path)
    {
        if (string.Equals(path, settings.Settings?.ProjectAbsoluteDir, StringComparison.OrdinalIgnoreCase)) return;
        unsaved.Leave("Some documents have unsaved changes. Save them before leaving the project?",
            () => session.QueueOpen(path));
    }

    static bool ActivateMenuItem(Gui gui)
    {
        if (gui.Pass != Pass.Pass2Render) return false;
        var interaction = gui.GetInteractable();
        if (interaction.OnHover()) gui.DrawBackgroundRect(Theme.Hover, 2);
        return interaction.OnClick();
    }

    static bool MenuAction(Gui gui, string icon, string id)
    {
        using (gui.Node(RowHeight, RowHeight, id).ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            gui.DrawText(icon, Theme.Text(11), Theme.InkDim);
            return ActivateMenuItem(gui);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var image in icons.Values) image?.Dispose();
    }

    /// <summary>
    /// A project's icon as its player settings name it, read once and kept; null when it has none or the
    /// image cannot be decoded.
    /// </summary>
    SKImage? IconFor(string projectDirectory)
    {
        if (icons.TryGetValue(projectDirectory, out var cached)) return cached;

        var image = ProjectIcon.FindSource(projectDirectory) is { } source ? SKImage.FromEncodedData(source) : null;
        icons[projectDirectory] = image;
        return image;
    }

    /// <summary>Starts this executable again, retaining its host arguments but replacing the project.</summary>
    static void OpenInNewInstance(string projectDirectory)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return;

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "--project" or "--scene" or "--dump" or "--script")
            {
                index++;
                continue;
            }

            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectDirectory);
        Process.Start(startInfo)?.Dispose();
    }
}
