namespace Gaya.Plugin.Turian;

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
    IEditorSettings editorSettings) : IChromeItem
{
    readonly Dictionary<string, SKImage?> icons = new(StringComparer.OrdinalIgnoreCase);

    bool open;
    Vector2 popupPosition;

    public void Render(Gui gui)
    {
        var current = settings.Settings?.ProjectAbsoluteDir;
        var theme = StudioTheme.Current;
        var rowHeight = theme.Scale(theme.RowHeight);

        using (gui.Node(theme.Scale(260), rowHeight, "project-switcher").Direction(Axis.Horizontal).Gap(4)
                   .ContentAlignY(0.5f).Enter())
        {
            var anchor = gui.CurrentNode.Rect;
            var label = current is null ? "No project" : Path.GetFileName(current);
            var button = gui.GetInteractable();
            if (current is not null && IconFor(current) is { } currentIcon) gui.Image(currentIcon, rowHeight, rowHeight);
            using (gui.Node().Expand().Enter())
                gui.DrawText(label, theme.Text(12), theme.InkDim, centerInRect: false);
            if (gui.Pass == Pass.Pass2Render && button.OnClick())
            {
                popupPosition = new Vector2(anchor.X, anchor.Y + anchor.H);

                // The open project's player settings may have been edited since its icon was read.
                if (current is not null) icons.Remove(current);
                open = true;
            }

            var projects = recent.Paths.Where(Directory.Exists).ToArray();
            var close = false;
            gui.Popup(ref open, () =>
            {
                using (gui.Node().Direction(Axis.Vertical).Gap(3).Enter())
                {
                    foreach (var project in projects)
                    {
                        using (gui.Node(theme.Scale(254), rowHeight, $"project-switcher/{project}")
                                   .Direction(Axis.Horizontal).Gap(3).Enter())
                        {
                            var row = gui.CurrentNode;
                            if (IconFor(project) is { } icon) gui.Image(icon, rowHeight, rowHeight);
                            else using (gui.Node(rowHeight, rowHeight).Enter()) { }

                            var labelWidth = theme.Scale(196) - rowHeight - theme.Scale(3);
                            if (gui.Button(Path.GetFileName(project), width: labelWidth, height: rowHeight))
                            {
                                if (!string.Equals(project, current, StringComparison.OrdinalIgnoreCase))
                                    session.Open(project);
                                close = true;
                            }

                            if (gui.IconButton("↗", size: rowHeight))
                            {
                                OpenInNewInstance(project);
                                close = true;
                            }

                            if (gui.IconButton("×", size: rowHeight))
                            {
                                if (recent.Remove(project))
                                    editorSettings.NotifyChanged("gaya.turian.recentProjects");
                                close = true;
                            }

                            gui.Tooltip(row, project, maxWidth: 1000);
                        }
                    }

                    if (gui.Button("Open Project…", width: theme.Scale(254), height: rowHeight))
                    {
                        commands.Execute("gaya.turian.openProject");
                        close = true;
                    }
                }
            }, width: theme.Scale(286), height: (projects.Length + 1) * (rowHeight + theme.Scale(3)) + theme.Scale(13),
                position: popupPosition,
                backgroundColor: theme.Panel, borderColor: theme.Border);

            if (close) open = false;
        }
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
