namespace Gaya.Plugin.Turian;

/// <summary>Lists and runs project component tests and displays failures at their source line.</summary>
sealed class TestRunnerPanel(SettingsService settings, BackgroundTaskRunner tasks, PlayModeService play,
    BuildManager build, ILogger logger) : IPanel
{
    sealed record Completion(string Project, SceneTestReport Report);
    Completion? completed;
    Completion? shown;
    SceneTestReport report = new([]);
    string filter = string.Empty;
    string project = string.Empty;
    string? selected;
    string[] source = [];
    int sourceStart;
    volatile bool running;
    bool frameRunning;
    bool canRun;
    Action? pending;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        if (gui.Pass == Pass.Pass1Build) { Refresh(); }
        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(5f).Padding(8f, 6f).Enter())
        {
            Toolbar(gui);
            Line(gui, frameRunning ? "Running component tests…" : $"{report.Tests.Count} tests; "
                + $"{report.Tests.Count(static item => item.Status == SceneTestStatus.Failed)} failed", "summary");
            using (gui.Node().Expand().Direction(Axis.Vertical).Enter())
            {
                gui.ScrollY();
                for (var index = 0; index < report.Tests.Count; index++) { Row(gui, report.Tests[index], index); }
                Details(gui);
            }
        }
        if (gui.Pass == Pass.Pass2Render && pending is { } action)
        {
            pending = null;
            action();
        }
    }

    void Toolbar(Gui gui)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(-1, theme.Scale(28f), "tests/actions").ExpandWidth().Direction(Axis.Horizontal).Gap(6f).Enter())
        {
            gui.TextInput(ref filter, placeholder: "Filter tests",
                id: "tests/filter").ExpandWidth().Height(theme.Scale(26f));
            if (!canRun) { return; }
            if (StudioControls.SmallTextButton(gui, "Discover", "tests/discover", theme.Scale(82f)))
            {
                pending = () => Start(listOnly: true);
            }
            if (StudioControls.SmallTextButton(gui, "Run", "tests/run", theme.Scale(62f)))
            {
                pending = () => Start(listOnly: false);
            }
        }
    }

    void Refresh()
    {
        CaptureControls();
        var root = settings.Settings?.ProjectAbsoluteDir ?? string.Empty;
        if (project != root)
        {
            project = root;
            report = new([]);
            shown = null;
            selected = null;
            source = [];
        }
        var next = Volatile.Read(ref completed);
        if (next is null || ReferenceEquals(next, shown) || next.Project != project) { return; }
        shown = next;
        report = next.Report;
        SelectDefault();
    }

    void SelectDefault() => Select(report.Tests.FirstOrDefault(static test => test.Status == SceneTestStatus.Failed)
        ?? report.Tests.FirstOrDefault());

    void CaptureControls()
    {
        frameRunning = running;
        canRun = settings.Settings is not null && !frameRunning && !play.IsActive && !build.IsPlaying;
    }

    void Start(bool listOnly)
    {
        if (settings.Settings is not { } current || running) { return; }
        var snapshot = (BuildAppSettings)new BuildAppSettings().Load(current);
        var selection = filter;
        running = true;
        tasks.RunWithResult(new BackgroundTaskSpec
        {
            Label = "Component tests",
            Kind = BackgroundTaskKind.Build,
            Locks = EditorLocks.Project,
            BlocksUi = true,
            Policy = DuplicatePolicy.Drop,
        }, async (_, token) =>
        {
            try
            {
                var result = await new ProjectTestRunner(logger).RunAsync(snapshot, selection, listOnly, token)
                    .ConfigureAwait(false);
                Volatile.Write(ref completed, new Completion(snapshot.ProjectAbsoluteDir, result));
                return new BackgroundTaskResult(listOnly
                    ? result.Tests.All(static test => test.Status != SceneTestStatus.Failed) : result.Succeeded,
                    $"{result.Tests.Count} component tests");
            }
            finally { running = false; }
        });
    }

    void Row(Gui gui, SceneTestResult test, int index)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(-1, theme.Scale(26f), $"tests/row/{index}").ExpandWidth().Padding(4f, 2f).Enter())
        {
            var hit = gui.GetInteractable();
            if (gui.Pass == Pass.Pass2Render)
            {
                if (selected == test.Name || hit.OnHover()) { gui.DrawBackgroundRect(theme.Hover); }
                if (hit.OnClick()) { pending = () => Select(test); }
            }
            gui.DrawText($"{test.Status}: {test.Name}", theme.Text(12f), theme.Ink, centerInRect: false);
        }
    }

    void Select(SceneTestResult? test)
    {
        selected = test?.Name;
        source = [];
        if (test?.SourceFile is not { } path || test.SourceLine <= 0 || !File.Exists(path)) { return; }
        try
        {
            sourceStart = Math.Max(1, test.SourceLine - 3);
            source = [.. File.ReadLines(path).Select(static (line, index) => $"{index + 1}: {line}")
                .Skip(sourceStart - 1).Take(7)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { source = [error.Message]; }
    }

    void Details(Gui gui)
    {
        var test = report.Tests.FirstOrDefault(test => test.Name == selected);
        if (test is null) { return; }
        Line(gui, test.Message ?? test.Status.ToString(), "message");
        SourceLocation(gui, test);
        for (var index = 0; index < source.Length; index++)
        {
            Line(gui, source[index], $"source/{index}", highlight: sourceStart + index == test.SourceLine);
        }
        if (test.StackTrace is not null)
        {
            var lines = test.StackTrace.Split('\n');
            for (var index = 0; index < lines.Length; index++) { Line(gui, lines[index], $"stack/{index}"); }
        }
    }

    void SourceLocation(Gui gui, SceneTestResult test)
    {
        if (test.SourceFile is not null)
        {
            Line(gui, $"{test.SourceFile}({test.SourceLine}):", "location");
            if (StudioControls.SmallTextButton(gui, "Open Source", "tests/openSource", 110f))
            {
                pending = () => LogSource.Open($"{test.SourceFile}({test.SourceLine}):", logger, project);
            }
        }
    }

    static void Line(Gui gui, string text, string id, bool highlight = false)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(-1, theme.Scale(24f), $"tests/{id}").ExpandWidth().Enter())
        {
            if (highlight) { gui.DrawBackgroundRect(theme.Hover); }
            gui.DrawText(text, theme.Text(12f), theme.Ink, centerInRect: false);
        }
    }
}
