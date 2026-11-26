namespace Turian.Tests;

/// <summary>Opening an asset follows its kind: scripts go to the external editor, and nothing else changes.</summary>
public sealed class AssetOpenServiceTests : IDisposable
{
    readonly AssetBrowserHarness harness = new();
    readonly DummyEditor editor = new("chosen");

    /// <inheritdoc/>
    public void Dispose() => harness.Dispose();

    void ChooseEditor(DummyEditor chosen)
    {
        var program = Path.Combine(harness.Project, "editor");
        File.WriteAllText(program, string.Empty);
        harness.Registry.Register(chosen);
        harness.Launcher.Selection = new ExternalEditorSelection(chosen.Id, program);
    }

    static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return false;
    }

    /// <summary>A script opens in the chosen editor with its project and file, not in the desktop's default program.</summary>
    [Fact]
    public async Task ScriptOpensInTheExternalEditor()
    {
        ChooseEditor(editor);
        var script = Path.Combine(harness.Root, "Player ü.cs");
        File.WriteAllText(script, "class Player;");

        Assert.Equal(AssetActivation.ExternalProgram, harness.Opener.Open(script, null));

        Assert.True(await Eventually(() => editor.Opened.Count == 1));
        var context = editor.Opened[0];
        Assert.Equal(script, context.File);
        Assert.Equal(harness.Project, context.ProjectDirectory);
    }

    /// <summary>The extension check ignores case, so <c>Player.CS</c> is a script too.</summary>
    [Fact]
    public async Task UpperCaseExtensionIsStillAScript()
    {
        ChooseEditor(editor);
        var script = Path.Combine(harness.Root, "Player.CS");
        File.WriteAllText(script, "class Player;");

        harness.Opener.Open(script, null);

        Assert.True(await Eventually(() => editor.Opened.Count == 1));
    }

    /// <summary>An editor that cannot start is reported as a notification and the double-click does not throw.</summary>
    [Fact]
    public async Task ScriptThatCannotBeOpenedIsReported()
    {
        ChooseEditor(new DummyEditor("broken") { Failure = new IOException("no such program") });
        var script = Path.Combine(harness.Root, "Player.cs");
        File.WriteAllText(script, "class Player;");

        harness.Opener.Open(script, null);

        EditorNotification? failure = null;
        Assert.True(await Eventually(() => harness.Notifications.TryTake(out failure)));
        Assert.True(failure!.IsError);
        Assert.Contains("no such program", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Scenes open as documents, in the workspace, and are not sent to any editor.</summary>
    [Fact]
    public void SceneOpensInTheStudio()
    {
        ChooseEditor(editor);

        var activation = harness.Opener.Open(Path.Combine(harness.Root, "Level.prefab"), new TemporaryScene("Level"));

        Assert.Equal(AssetActivation.Edit, activation);
        Assert.Empty(editor.Opened);
    }

    /// <summary>Materials are inspected in the studio, and are not sent to any editor.</summary>
    [Fact]
    public void MaterialIsInspected()
    {
        ChooseEditor(editor);
        var material = Path.Combine(harness.Root, "Wall.material");
        File.WriteAllText(material, "{}");

        Assert.Equal(AssetActivation.Inspect, harness.Opener.Open(material, null));
        Assert.Empty(editor.Opened);
    }

    /// <summary>A file nobody claims does nothing.</summary>
    [Fact]
    public void UnclaimedFileDoesNothing()
    {
        ChooseEditor(editor);

        Assert.Equal(AssetActivation.None, harness.Opener.Open(Path.Combine(harness.Root, "notes.unclaimed"), null));
        Assert.Empty(editor.Opened);
    }
    sealed class DocumentEditor : IAssetDocumentEditor
    {
        public bool FailCheck { get; set; }
        public bool FailOpen { get; set; }
        public List<string> Opened { get; } = [];
        public bool IsReadOnly { get; private set; }

        public bool CanOpen(string sourcePath) => FailCheck
            ? throw new InvalidOperationException()
            : sourcePath.EndsWith(".ui", StringComparison.OrdinalIgnoreCase);

        public void Open(string sourcePath, bool isReadOnly = false)
        {
            IsReadOnly = isReadOnly;
            if (FailOpen)
            {
                throw new InvalidOperationException();
            }

            Opened.Add(sourcePath);
        }
    }

    /// <summary>Document editors use the catalog's existing target and reveal the corresponding panel.</summary>
    [Fact]
    public void MatchingEditorOpensAndRevealsItsPanel()
    {
        var target = new DocumentEditor();
        var pages = (IList<UserPanelPage>)harness.Panels.Pages;
        pages.Add(new UserPanelPage("fixture.panel", "Fixture", "Tools/Fixture", target));
        var shown = new List<string>();
        using var bridge = new UserPanelBridge(harness.Panels, new PanelRegistry(), new CommandRegistry(),
            new MenuRegistry(), NullLogger.Instance, shown.Add);
        var path = Path.Combine(harness.Root, "screen.ui");

        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(path, null));
        Assert.Equal([path], target.Opened);
        Assert.False(target.IsReadOnly);
        Assert.Equal(["fixture.panel"], shown);
        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(new AssetEntry(path, false, null, null, true)));
        Assert.True(target.IsReadOnly);
        bridge.Dispose();
        harness.Opener.Open(path, null);
        Assert.Equal(2, shown.Count);
        pages.Clear();
        Assert.Null(harness.Panels.OpenDocument(path));
    }

    /// <summary>Documents and style sheets route to the editor that accepts them; anything else keeps its own opening.</summary>
    [Fact]
    public void RoutingFollowsWhatTheEditorAccepts()
    {
        var target = new AssetDocumentHostTests.DocumentFixture();
        ((IList<UserPanelPage>)harness.Panels.Pages).Add(new UserPanelPage("doc.panel", "Doc", "Tools/Doc", target));
        var ui = Path.Combine(harness.Root, "screen.ui");
        var unclaimed = Path.Combine(harness.Root, "notes.unclaimed");

        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(ui, null));
        Assert.Equal(ui, target.DocumentPath);
        var pss = Path.Combine(harness.Root, "theme.pss");
        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(pss, null));
        Assert.Equal(pss, target.DocumentPath);
        Assert.Equal(AssetActivation.None, harness.Opener.Open(unclaimed, null));
        Assert.Equal(pss, target.DocumentPath);
    }

    /// <summary>A broken predicate is skipped; a claiming editor failure does not fall back.</summary>
    [Fact]
    public void EditorFailuresDoNotCrashOrFallBack()
    {
        var pages = (IList<UserPanelPage>)harness.Panels.Pages;
        pages.Add(new UserPanelPage("bad.check", "Bad", "Tools/Bad", new DocumentEditor { FailCheck = true }));
        var target = new DocumentEditor { FailOpen = true };
        pages.Add(new UserPanelPage("bad.open", "Bad", "Tools/Bad", target));
        var path = Path.Combine(harness.Root, "screen.ui");

        Assert.Equal(AssetActivation.None, harness.Opener.Open(path, null));
        Assert.Empty(target.Opened);
        Assert.Contains(harness.PanelLog.ReceivedCalls(), static call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
        target.FailOpen = false;
        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(path, null));
        Assert.Equal([path], target.Opened);
    }

    /// <summary>Registered package policy also applies when opening a source path directly.</summary>
    [Fact]
    public void RegisteredPackagePolicyReachesDocumentEditor()
    {
        var package = Path.Combine(harness.Project, "package");
        var roots = new List<(string Root, bool ReadOnly)> { (package, true) };
        typeof(AssetImporter).GetField("packageRoots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(harness.Importer, roots);
        var target = new DocumentEditor();
        ((IList<UserPanelPage>)harness.Panels.Pages)
            .Add(new UserPanelPage("package.panel", "Package", "Tools/Package", target));
        var path = Path.Combine(package, "screen.ui");

        Assert.True(harness.Importer.IsSourceReadOnly(path));
        Assert.False(harness.Importer.IsSourceReadOnly(Path.Combine(harness.Root, "screen.ui")));
        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(path, null));
        Assert.True(target.IsReadOnly);
    }

}
