namespace Turian.Tests;

/// <summary>The External Editor row of the Settings panel, driven through the real panel and the real settings.</summary>
[Collection(SerialTests.Name)]
public sealed class ExternalEditorPanelTests : IDisposable
{
    const string Field = $"settings/{ExternalEditorSettings.PageId}/field0";

    readonly string root = Directory.CreateTempSubdirectory("turian-editor-panel-").FullName;
    readonly WorkbenchFrame frame = new();
    readonly FakeEditor fake;
    // Only the custom command ships here: the real defaults find whatever editors this machine has installed.
    readonly ExternalEditorRegistry registry = new(NullLogger.Instance, [new CustomEditor()]);
    readonly ExternalEditorLauncher launcher;
    readonly ExternalEditorSettings page = new();
    readonly SettingsService project = new();
    readonly ExternalEditorFieldEditor editor;
    readonly ExternalEditorBridge bridge;
    readonly FakePicker picker = new();

    sealed class FakePicker : IProgramPicker
    {
        public Action<string?>? Pending { get; private set; }

        public int Asked { get; private set; }

        public void Pick(string title, Action<string?> onComplete)
        {
            Asked++;
            Pending = onComplete;
        }
    }

    sealed class FakeEditor(string executable) : IExternalEditor
    {
        public TaskCompletionSource<EditorContext> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "fake";

        public string DisplayName => "Fake Editor";

        public ExternalEditorCapabilities Capabilities => ExternalEditorCapabilities.OpenProject;

        public IReadOnlyList<ExternalEditorOption> Options { get; } =
        [
            new("reuse", "Reuse the open window", ExternalEditorOptionKind.Toggle, "false", "Opens files in the window that is already running."),
            new("flags", "Extra flags", ExternalEditorOptionKind.Text, "--wait"),
            new("window", "Window", ExternalEditorOptionKind.Choice, "new-unless-open", "Which window to use.",
                [new("new-unless-open", "Unless open"), new("new", "Always new"), new("reuse", "Reuse")]),
        ];

        public bool Recognizes(string path) => Path.GetFileName(path).StartsWith("fake", StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<EditorInstallation> Discover() => [new(Id, DisplayName, executable, "1.0")];

        public Task OpenAsync(EditorInstallation installation, EditorContext context, CancellationToken cancellationToken)
        {
            Opened.TrySetResult(context);
            return Task.CompletedTask;
        }
    }

    /// <summary>Opens the Settings panel on the Personal tab, where the page lives, with only this page registered.</summary>
    public ExternalEditorPanelTests()
    {
        var executable = Path.Combine(root, "Mi Editor é", "fake é");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        fake = new FakeEditor(executable);
        registry.Register(fake);
        // No test may start a real program: the fallback to the operating system's default is replaced.
        launcher = new ExternalEditorLauncher(registry, new EditorNotifications(), NullLogger.Instance)
        {
            OpenWithSystem = static _ => Task.CompletedTask,
        };
        editor = new ExternalEditorFieldEditor(registry, launcher, picker, project, null);
        bridge = new ExternalEditorBridge(page, launcher, frame.Settings, frame.FieldEditors, editor, registry);

        using var shell = PluginHost.Load([], NullLogger.Instance);
        frame.Panels.Register(shell.Panels.All.Single(static panel => panel.Id == ShellPanels.Settings));
        foreach (var other in frame.Settings.Pages.ToArray())
        {
            frame.Settings.Remove(other.Id);
        }

        frame.Settings.Register(page);
        bridge.Start();
        frame.Draw();
        frame.Workbench.ShowPanel(ShellPanels.Settings);
        frame.Draw();
        frame.Draw();
        Click(Nodes().Single(static node => node.Id == "settings/scope/Personal").Rect.Center);
        frame.Draw();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        bridge.Dispose();
        frame.Dispose();
        PrecastBrickFixture.DeleteTree(root);
    }

    string Executable => fake.Discover()[0].Executable;

    IEnumerable<LayoutNode> Nodes() => WorkbenchFrame.Descendants(frame.Gui.RootNode!);

    LayoutNode Node(string fragment) => Nodes().First(node => node.Id.Contains(fragment, StringComparison.Ordinal));

    bool Has(string fragment) => Nodes().Any(node => node.Id.Contains(fragment, StringComparison.Ordinal));

    void Click(Vector2 pointer)
    {
        frame.Input.MousePosition.Returns(pointer);
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Draw();
        frame.Draw();
    }

    /// <summary>Picks a dropdown entry; a negative index counts from the end, where Browse and Custom always are.</summary>
    void ChooseEntry(int index)
    {
        var button = WorkbenchFrame.Descendants(Node($"{Field}/choice"))
            .Single(static node => node.Id.EndsWith("/button", StringComparison.Ordinal));
        var prefix = button.Id[..^"/button".Length];
        Click(button.Rect.Center);
        var count = Nodes().Count(node => node.Id.StartsWith($"{prefix}/list/", StringComparison.Ordinal)
                                          && int.TryParse(node.Id[$"{prefix}/list/".Length..], out _));
        Click(Nodes().Single(node => node.Id == $"{prefix}/list/{(index >= 0 ? index : count + index)}").Rect.Center);
    }

    /// <summary>Choosing an installation applies at once: the page, the launcher and the file all follow, with no restart.</summary>
    [Fact]
    public void ChoosingAnInstallationAppliesAtOnce()
    {
        // 0 System default, 1 the editor's heading, 2 its installation, then other editors, Browse and Custom.
        ChooseEntry(2);

        Assert.Equal(new ExternalEditorChoice("fake", Executable), page.Editor);
        Assert.Equal("fake", launcher.Selection!.EditorId);
        Assert.Equal(Executable, launcher.Selection.Executable);
        frame.Settings.Save();
        Assert.Contains(ExternalEditorSettings.PageId, File.ReadAllText(frame.Settings.PathFor(SettingsScope.User)),
            StringComparison.Ordinal);
    }

    /// <summary>Choosing the system default clears the launcher's selection again.</summary>
    [Fact]
    public void ChoosingTheSystemDefaultClearsTheSelection()
    {
        ChooseEntry(2);
        ChooseEntry(0);

        Assert.True(page.Editor.UsesSystemDefault);
        Assert.Null(launcher.Selection);
    }

    /// <summary>The Custom entry starts a custom command with the default template and shows its fields.</summary>
    [Fact]
    public void CustomEntryShowsTheCustomFields()
    {
        ChooseEntry(-1);

        Assert.Equal(CustomEditor.EditorId, page.Editor.EditorId);
        Assert.Equal(CustomEditor.DefaultTemplate, page.Editor.Arguments);
        Assert.True(Has($"{Field}/program"));
        Assert.True(Has($"{Field}/arguments"));
    }

    /// <summary>A missing executable shows the warning with a re-pick action; an editor that is gone offers the default.</summary>
    [Fact]
    public void MissingExecutableAndMissingEditorAreWarnedAbout()
    {
        page.Editor = new ExternalEditorChoice("fake", Path.Combine(root, "gone", "fake"));
        frame.Draw();
        Assert.True(Has($"{Field}/warning"));

        page.Editor = new ExternalEditorChoice("fake", Executable);
        frame.Draw();
        frame.Draw();
        Assert.False(Has($"{Field}/warning"));

        page.Editor = new ExternalEditorChoice("removed-brick", Executable);
        frame.Draw();
        Click(Node($"{Field}/fix").Rect.Center);
        Assert.True(page.Editor.UsesSystemDefault);
    }

    /// <summary>An option the editor offers is drawn and its value is stored with the choice.</summary>
    [Fact]
    public void EditorOptionsAreStoredWithTheChoice()
    {
        page.Editor = new ExternalEditorChoice("fake", Executable);
        frame.Draw();
        frame.Draw();

        Click(Node($"{Field}/option/reuse").Rect.Center);

        Assert.Equal("true", page.Editor.Options!["reuse"]);
        Assert.Equal("true", launcher.Selection!.Options!["reuse"]);
    }

    /// <summary>A choice option is a dropdown of its values, and the pick is stored with the choice and reaches the launcher.</summary>
    [Fact]
    public void ChoiceOptionStoresThePickedValue()
    {
        page.Editor = new ExternalEditorChoice("fake", Executable);
        frame.Draw();
        frame.Draw();

        var button = WorkbenchFrame.Descendants(Node($"{Field}/option/window"))
            .Single(static node => node.Id.EndsWith("/button", StringComparison.Ordinal));
        var prefix = button.Id[..^"/button".Length];
        Click(button.Rect.Center);
        Click(Nodes().Single(node => node.Id == $"{prefix}/list/2").Rect.Center);

        Assert.Equal("reuse", page.Editor.Options!["window"]);
        Assert.Equal("reuse", launcher.Selection!.Options!["window"]);
    }

    /// <summary>Test opens the project through the launcher with the chosen editor.</summary>
    [Fact]
    public async Task TestOpensTheProject()
    {
        project.Set(new AppSettings { ProjectAbsoluteDir = root });
        page.Editor = new ExternalEditorChoice("fake", Executable);
        frame.Settings.NotifyChanged(ExternalEditorSettings.PageId);
        frame.Draw();
        frame.Draw();

        Click(Node($"{Field}/test").Rect.Center);

        var context = await fake.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(root, context.ProjectDirectory);
    }

    /// <summary>
    /// The Browse entry asks for a program and nothing changes until one is picked; a program an editor recognises
    /// becomes that editor, and cancelling leaves the choice alone.
    /// </summary>
    [Fact]
    public void BrowseEntryAsksForAProgramAndAppliesWhatIsPicked()
    {
        ChooseEntry(-2);
        Assert.Equal(1, picker.Asked);
        Assert.True(page.Editor.UsesSystemDefault);

        picker.Pending!(null);
        Assert.True(page.Editor.UsesSystemDefault);

        picker.Pending!(Executable);
        Assert.Equal(new ExternalEditorChoice("fake", Executable), page.Editor);
    }

    /// <summary>
    /// The custom program's Browse button and the warning's re-pick keep the custom editor and replace only its program,
    /// and Rescan refreshes without changing anything.
    /// </summary>
    [Fact]
    public void CustomBrowseAndQuickFixReplaceOnlyTheProgram()
    {
        var gone = Path.Combine(root, new string('x', 160), "ed");
        page.Editor = new ExternalEditorChoice(CustomEditor.EditorId, gone, "{file}");
        frame.Draw();
        frame.Draw();
        Assert.True(Has($"{Field}/warning"));

        Click(Node($"{Field}/program/browse").Rect.Center);
        Assert.Equal(1, picker.Asked);
        picker.Pending!(Executable);
        Assert.Equal(new ExternalEditorChoice(CustomEditor.EditorId, Executable, "{file}"), page.Editor);

        page.Editor = new ExternalEditorChoice(CustomEditor.EditorId, gone, "{file}");
        frame.Draw();
        frame.Draw();
        Click(Node($"{Field}/fix").Rect.Center);
        Assert.Equal(2, picker.Asked);
        picker.Pending!(null);
        Assert.Equal(gone, page.Editor.Executable);

        Click(Node($"{Field}/rescan").Rect.Center);
        Assert.Equal(gone, page.Editor.Executable);
    }

    /// <summary>The editor's text option shows its default, and a change to the registry refreshes the installations.</summary>
    [Fact]
    public void TextOptionsShowTheirDefaultAndRegistryChangesRefreshTheList()
    {
        page.Editor = new ExternalEditorChoice("fake", Executable);
        frame.Draw();
        frame.Draw();

        Assert.True(Has($"{Field}/option/flags"));
        Assert.Equal("--wait", fake.Options[1].ValueIn(page.Editor.Options));

        registry.Register(fake);
        frame.Draw();
        Assert.True(Has($"{Field}/choice"));
    }

    /// <summary>The studio's picker opens the file dialog without completing it until the user chooses.</summary>
    [Fact]
    public void StudioPickerOpensTheFileDialog()
    {
        var completed = false;

        new FileDialogProgramPicker(new FileDialogChrome()).Pick("Choose", _ => completed = true);

        Assert.False(completed);
    }

    /// <summary>A browsed program becomes the editor that recognises it, otherwise a custom command.</summary>
    [Fact]
    public void BrowsedProgramBecomesTheEditorThatRecognisesIt()
    {
        var current = new ExternalEditorChoice("fake", Executable);

        Assert.Equal(new ExternalEditorChoice("fake", "/x/fake-2"), editor.Browsed(new ExternalEditorChoice(), "/x/fake-2", keepEditor: false));
        Assert.Equal(new ExternalEditorChoice(CustomEditor.EditorId, "/x/other", CustomEditor.DefaultTemplate),
            editor.Browsed(new ExternalEditorChoice(), "/x/other", keepEditor: false));
        Assert.Equal(current with { Executable = "/x/relocated" }, editor.Browsed(current, "/x/relocated", keepEditor: true));
    }
}
