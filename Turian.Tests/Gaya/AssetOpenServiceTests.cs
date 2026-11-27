namespace Turian.Tests;

/// <summary>
/// Opening an asset follows its kind: scripts go to the external editor, claimed files open as documents and the rest
/// follow their asset type.
/// </summary>
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

    /// <summary>
    /// With the UI brick's document kind registered, style sheets and text files still go to the program the desktop
    /// opens them with, while only the UI document opens as a tab.
    /// </summary>
    [Theory]
    [InlineData("theme.pss")]
    [InlineData("README.md")]
    [InlineData("LICENSE.txt")]
    [InlineData("table.csv")]
    public void TextSourcesOpenInTheSystemProgramBesideTheUiDocumentKind(string name)
    {
        var kind = new FakeDocumentKind { Id = "turian.ui", Extension = ".ui" };
        using var registration = harness.Kinds.Add(kind);
        var launched = new List<ProcessStartInfo>();
        harness.Opener.Launch = launched.Add;
        var source = Source(name);

        var activation = harness.Opener.Open(source, null);

        Assert.Equal(AssetActivation.ExternalProgram, activation);
        var start = Assert.Single(launched);
        Assert.Equal(source, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Null(harness.Workspace.KindFor(source));
        Assert.Empty(harness.Workspace.Documents);
        Assert.Empty(kind.Opened);

        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(Source("menu.ui"), null));
        Assert.Single(kind.Opened);
        Assert.Single(launched);
    }

    /// <summary>A system without a program for the file is logged, and the double click does not throw.</summary>
    [Fact]
    public void AMissingSystemProgramIsNotAnError()
    {
        harness.Opener.Launch = static _ => throw new InvalidOperationException("no program");

        Assert.Equal(AssetActivation.ExternalProgram, harness.Opener.Open(Source("notes.txt"), null));
    }

    /// <summary>Unless replaced, a program is started with the request as it is given.</summary>
    [Fact]
    public void TheDefaultLaunchStartsTheProgram()
    {
        var request = new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true };

        harness.Opener.Launch(request);

        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            harness.Opener.Launch(new ProcessStartInfo(Path.Combine(harness.Root, "no-such-program"))));
    }

    /// <summary>A file a kind claims opens as its tab, and goes back to its own opening when the kind leaves.</summary>
    [Fact]
    public void ADocumentKindDecidesHowItsFilesOpenWhileItIsRegistered()
    {
        var launched = new List<ProcessStartInfo>();
        harness.Opener.Launch = launched.Add;
        var source = Source("menu.ui");
        var kind = new FakeDocumentKind { Extension = ".ui" };
        var registration = harness.Kinds.Add(kind);

        Assert.Equal(AssetActivation.Edit, harness.Opener.Open(new AssetEntry(source, false, null, null, true)));
        Assert.True(harness.Workspace.Active!.IsReadOnly);
        harness.Workspace.Close(harness.Workspace.Active);
        registration.Dispose();

        Assert.Equal(AssetActivation.ExternalProgram, harness.Opener.Open(source, null));
        Assert.Single(launched);
        Assert.Single(kind.Opened);
    }

    /// <summary>A new file is imported, so a document kind finds its metadata, and opens as a double click would.</summary>
    [Fact]
    public void ACreatedFileIsImportedAndOpened()
    {
        var kind = new FakeDocumentKind();
        using var registration = harness.Kinds.Add(kind);
        var source = Path.Combine(harness.Root, "made.fake");
        File.WriteAllText(source, string.Empty);

        Assert.Equal(AssetActivation.Edit, harness.Opener.OpenCreated(source));

        Assert.True(File.Exists(source + ".meta"));
        Assert.Equal(source, Assert.Single(kind.Opened).SourcePath);
        Assert.Same(harness.Workspace.Active!.Document, kind.Opened[0]);
    }

    /// <summary>A new folder has nothing to open, and a new asset whose metadata is unreadable opens nothing.</summary>
    [Fact]
    public void ACreatedEntryThatCannotOpenDoesNothing()
    {
        var source = Path.Combine(harness.Root, "broken.prefab");
        File.WriteAllText(source, string.Empty);
        File.WriteAllText(source + ".meta", "not json");
        var inspections = new AssetInspectionService(harness.Importer, new AssetTypeCatalog(), harness.Settings,
            null!, NullLogger.Instance);
        var withoutImporter = new AssetOpenService(new AssetTypeCatalog(), harness.Workspace, inspections,
            harness.Inspector, null!, NullLogger.Instance);

        Assert.Equal(AssetActivation.None, harness.Opener.OpenCreated(Path.Combine(harness.Root, "Folder")));
        Assert.Equal(AssetActivation.None, withoutImporter.OpenCreated(source));
        Assert.Empty(harness.Workspace.Documents);
    }

    string Source(string name)
    {
        var path = Path.Combine(harness.Root, name);
        File.WriteAllText(path, string.Empty);
        File.WriteAllText(path + ".meta",
            Serializer.Serialize(new Asset { Id = Guid.NewGuid(), RelativePath = path }));
        return path;
    }
}
