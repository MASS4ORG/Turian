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
}
