namespace Turian.Tests;

/// <summary>Checks that Save, Save All, Undo and Redo reach the focused document editor and otherwise the scene.</summary>
[Collection(SerialTests.Name)]
public sealed class StudioCommandsRoutingTests : IDisposable
{
    readonly AssetBrowserHarness harness = new();
    readonly AssetDocumentHost host;
    readonly ServiceProvider services;
    readonly IPluginContext context = Substitute.For<IPluginContext>();
    readonly AssetDocumentHostTests.DocumentFixture document = new()
    {
        DocumentPath = "/project/Assets/Menu.ui",
    };

    /// <summary>Registers the studio commands over a host that sees one document editor.</summary>
    public StudioCommandsRoutingTests()
    {
        host = new AssetDocumentHost(harness.Panels, harness.Settings, harness.Assets, NullLogger.Instance);
        ((IList<UserPanelPage>)harness.Panels.Pages).Add(new UserPanelPage("doc.panel", "Doc", "Tools/Doc", document));
        services = new ServiceCollection().AddSingleton(host).AddSingleton(harness.Workspace)
            .AddSingleton(harness.Undo).AddSingleton(new StudioLocalization()).BuildServiceProvider();
        StudioCommands.Register(context);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        services.Dispose();
        host.Dispose();
        harness.Dispose();
    }

    CommandDescriptor Command(string id) => context.Commands.ReceivedCalls()
        .Where(static call => call.GetMethodInfo().Name == nameof(ICommandRegistry.Register))
        .Select(static call => (CommandDescriptor)call.GetArguments()[0]!).Last(command => command.Id == id);

    bool CanRun(string id) => Command(id).CanExecute!(services);

    void Run(string id) => Command(id).Execute(services);

    void Focus(string? panelId) => host.FocusedPanel = () => panelId;

    /// <summary>Save writes the focused document editor's document and is available whenever one has focus.</summary>
    [Fact]
    public void SaveWritesTheFocusedDocument()
    {
        document.IsDirty = true;
        Assert.False(CanRun("gaya.turian.save"));

        Focus("doc.panel");
        Assert.True(CanRun("gaya.turian.save"));
        Run("gaya.turian.save");

        Assert.Equal(1, document.Saves);
        Assert.False(document.IsDirty);
    }

    /// <summary>With another panel focused, Save leaves the document editor alone.</summary>
    [Fact]
    public void SaveIgnoresADocumentWithoutFocus()
    {
        document.IsDirty = true;
        Focus("scene.tree");

        Run("gaya.turian.save");

        Assert.Equal(0, document.Saves);
        Assert.True(document.IsDirty);
    }

    /// <summary>Save All is available for an open document and writes every dirty one.</summary>
    [Fact]
    public void SaveAllIncludesDocumentEditors()
    {
        Assert.True(CanRun("gaya.turian.saveAll"));
        document.IsDirty = true;

        Run("gaya.turian.saveAll");

        Assert.Equal(1, document.Saves);
        document.DocumentPath = null;
        Assert.False(CanRun("gaya.turian.saveAll"));
    }

    /// <summary>Undo and Redo go to the focused editor's own stack, with the scene's history untouched.</summary>
    [Fact]
    public void UndoAndRedoFollowFocus()
    {
        document.Undos = 1;
        Focus("doc.panel");

        Assert.True(CanRun("gaya.turian.undo"));
        Assert.False(CanRun("gaya.turian.redo"));
        Assert.Equal("Undo", Command("gaya.turian.undo").MenuLabelFor(services));
        Run("gaya.turian.undo");

        Assert.Equal((1, 0), (document.UndoCalls, document.Undos));
        Assert.True(CanRun("gaya.turian.redo"));
        Run("gaya.turian.redo");
        Assert.Equal(1, document.RedoCalls);
    }

    /// <summary>Without focus the commands fall back to the scene, which has nothing to undo here.</summary>
    [Fact]
    public void UndoFallsBackToTheSceneWithoutFocus()
    {
        document.Undos = 1;
        Focus("scene.tree");

        Assert.False(CanRun("gaya.turian.undo"));
        Assert.False(CanRun("gaya.turian.redo"));
        Run("gaya.turian.undo");
        Run("gaya.turian.redo");

        Assert.Equal((0, 0), (document.UndoCalls, document.RedoCalls));
    }
}
