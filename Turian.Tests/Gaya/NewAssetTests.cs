namespace Turian.Tests;

/// <summary>
/// Creating an asset from File / New or the asset browser's Create menu writes it and opens it the way a double click
/// does.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class NewAssetTests : IDisposable
{
    sealed class Provider(params AssetCreationKind[] kinds) : IAssetCreationProvider
    {
        public IReadOnlyList<AssetCreationKind> Kinds { get; } = kinds;
    }

    readonly AssetBrowserHarness harness = new();
    readonly FakeDocumentKind documentKind = new() { Id = "turian.ui", Extension = ".ui" };
    readonly CommandRegistry commands = new();
    readonly MenuRegistry menus = new();
    readonly BuildManager build = new(new AppSettings(), NullLogger.Instance);
    readonly NewAssetMenuBridge bridge;
    readonly ServiceProvider services;
    readonly IDisposable kindRegistration;

    /// <summary>Registers a UI document kind and builds the File / New menu over the browser's services.</summary>
    public NewAssetTests()
    {
        kindRegistration = harness.Kinds.Add(documentKind);
        bridge = new NewAssetMenuBridge(harness.Creation, new EditorAssemblySet(build), commands, menus);
        services = new ServiceCollection().AddSingleton(harness.Settings).AddSingleton(harness.Creation)
            .AddSingleton(harness.Opener).BuildServiceProvider();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        bridge.Dispose();
        services.Dispose();
        kindRegistration.Dispose();
        build.Dispose();
        harness.Dispose();
    }

    static AssetCreationKind Document(string menuPath = "UI Document") => new(menuPath, "NewUI.ui")
    {
        Creator = static directory =>
        {
            var path = Path.Combine(directory, "NewUI.ui");
            File.WriteAllText(path, "<UI />");
            return path;
        },
    };

    MenuItemDescriptor Item(string label) => Assert.Single(menus.ItemsFor(MenuIds.File),
        item => commands.Find(item.CommandId)!.MenuLabel == label);

    void Choose(string label) => commands.Find(Item(label).CommandId)!.Execute(services);

    /// <summary>The File menu offers what can be created under New, keeping each entry's submenu.</summary>
    [Fact]
    public void FileNewListsEveryCreatableAsset()
    {
        using var contributed = harness.Creation.Add(new Provider(Document("Interface/UI Document")));

        bridge.Sync();
        bridge.Sync();

        Assert.Equal("New", Item("Scene").Path);
        Assert.Equal("New", Item("Material").Path);
        Assert.Equal("New/Interface", Item("UI Document").Path);
        Assert.DoesNotContain(menus.ItemsFor(MenuIds.File),
            item => commands.Find(item.CommandId)!.MenuLabel == "Folder");
        Assert.True(commands.Find(Item("Scene").CommandId)!.CanExecute!(services));
        harness.Settings.Set(new AppSettings());
        using var closed = new ServiceCollection().AddSingleton(new SettingsService()).BuildServiceProvider();
        Assert.False(commands.Find(Item("Scene").CommandId)!.CanExecute!(closed));
        commands.Find(Item("Scene").CommandId)!.Execute(closed);
    }

    /// <summary>The menu follows a brick's entries as they come and go, and leaves nothing behind.</summary>
    [Fact]
    public void FileNewFollowsContributedEntries()
    {
        bridge.Sync();
        var contributed = harness.Creation.Add(new Provider(Document()));
        bridge.Sync();
        var id = Item("UI Document").CommandId;

        contributed.Dispose();
        bridge.Sync();

        Assert.Null(commands.Find(id));
        Assert.DoesNotContain(menus.ItemsFor(MenuIds.File), item => item.CommandId == id);
        bridge.Dispose();
        Assert.Empty(menus.ItemsFor(MenuIds.File));
    }

    /// <summary>A new UI document opens in its own tab, and a new scene in a scene tab, both inside Assets.</summary>
    [Fact]
    public void FileNewOpensTheNewAssetAsATab()
    {
        using var contributed = harness.Creation.Add(new Provider(Document()));
        bridge.Sync();

        Choose("UI Document");

        var document = harness.Workspace.Active!;
        Assert.Equal(Path.Combine(harness.Root, "NewUI.ui"), document.SourcePath);
        Assert.Same(Assert.Single(documentKind.Opened), document.Document);

        Choose("Scene");

        var scene = harness.Workspace.Active!;
        Assert.Equal(WorkspaceDocumentKinds.Scene, scene.Kind);
        Assert.Equal(Path.Combine(harness.Root, "New Scene.prefab"), scene.SourcePath);
        Assert.Equal(2, harness.Workspace.Documents.Count);
    }

    /// <summary>The asset browser's Create menu writes into the current folder and opens the result.</summary>
    [Fact]
    public void TheBrowserCreateMenuOpensTheNewAssetAsATab()
    {
        harness.Invoke("Create", Document());

        Assert.Same(Assert.Single(documentKind.Opened), harness.Workspace.Active!.Document);

        harness.Invoke("Create", harness.Creation.Kinds.Single(static kind => kind.MenuPath == "Scene"));

        Assert.Equal(WorkspaceDocumentKinds.Scene, harness.Workspace.Active!.Kind);
        Assert.True(File.Exists(Path.Combine(harness.Root, "New Scene.prefab.meta")));
    }

    /// <summary>A new style sheet is handed to the system program, as a double click on one is, and opens no tab.</summary>
    [Fact]
    public void ANewStyleSheetOpensInTheSystemProgram()
    {
        var launched = new List<ProcessStartInfo>();
        harness.Opener.Launch = launched.Add;
        var style = new AssetCreationKind("Interface/UI Style Sheet", "NewStyle.pss")
        {
            Creator = static directory =>
            {
                var path = Path.Combine(directory, "NewStyle.pss");
                File.WriteAllText(path, "{}");
                return path;
            },
        };
        using var contributed = harness.Creation.Add(new Provider(style));
        bridge.Sync();

        Choose("UI Style Sheet");

        Assert.Equal(Path.Combine(harness.Root, "NewStyle.pss"), Assert.Single(launched).FileName);
        Assert.Empty(harness.Workspace.Documents);
    }

    /// <summary>An entry that writes nothing opens nothing.</summary>
    [Fact]
    public void AFailedCreationOpensNothing()
    {
        var failing = new AssetCreationKind("Broken", "Broken.ui") { Creator = static _ => null };
        using var contributed = harness.Creation.Add(new Provider(failing));
        bridge.Sync();

        Choose("Broken");
        harness.Invoke("Create", failing);

        Assert.Empty(harness.Workspace.Documents);
    }
}
