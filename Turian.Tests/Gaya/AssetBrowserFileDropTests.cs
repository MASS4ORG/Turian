namespace Turian.Tests;

/// <summary>Checks desktop imports and grid layout through complete browser frames.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetBrowserFileDropTests
{
    /// <summary>The default split grid starts at the top of its pane even inside a centered layout.</summary>
    [Fact]
    public void SplitGridStartsAtPaneOrigin()
    {
        Assert.Equal(AssetBrowserViewMode.Split, new AssetBrowserSettings().ViewMode);
        using var browser = new AssetBrowserHarness(AssetBrowserViewMode.Split);
        void Draw(Gui gui)
        {
            using (gui.Node(800, 500, "centered").AlignContent(0.5f, 0.5f).Enter()) browser.Panel.Render(gui);
        }
        browser.Frame(Draw);
        browser.Frame(Draw);
        var grid = browser.Find("assets/grid").Rect;
        var first = browser.Find("assets/tile/" + browser.Entry("Folder").AbsolutePath).Rect;
        Assert.Equal(grid.Y, first.Y, precision: 2);
        Assert.Equal(grid.X, first.X, precision: 2);
        Assert.DoesNotContain(browser.Nodes(), node => node.Id == "assets/view");
    }

    /// <summary>Assets without preview providers scale their icon glyphs together with the grid's zoom.</summary>
    [Fact]
    public void FallbackIconsScaleWithGridZoom()
    {
        using var browser = new AssetBrowserHarness();
        var id = "assets/preview/" + browser.Entry("a.txt").AbsolutePath;
        browser.Preferences.GridZoom = 32;
        browser.Frame();
        browser.Frame();
        var small = browser.Find(id).Children.Single().Rect.H;
        browser.Preferences.GridZoom = 128;
        browser.Frame();
        browser.Frame();
        Assert.True(browser.Find(id).Children.Single().Rect.H > small * 3);
    }

    /// <summary>Navigation and search share a row; filters need a second row only in narrow panels.</summary>
    [Theory]
    [InlineData(300, true)]
    [InlineData(650, true)]
    [InlineData(1000, false)]
    public void ToolbarUsesAtMostTwoRows(int width, bool compact)
    {
        using var browser = new AssetBrowserHarness(width: width);
        browser.Frame();
        var navigation = browser.Find("assets/navigation").Rect;
        var search = browser.Find("assets/searchField").Rect;
        Assert.Equal(navigation.Y, search.Y, precision: 2);
        Assert.True(search.X >= navigation.X + navigation.W);
        Assert.Equal(compact, browser.Nodes().Any(node => node.Id == "assets/filters"));
        Assert.InRange(browser.Find("assets/grid").Rect.Y, 0, compact ? 52 : 26);
    }

    /// <summary>A desktop drop targets the folder under the delivered pointer and is one undoable copy.</summary>
    [Theory]
    [InlineData(AssetBrowserViewMode.Grid)]
    [InlineData(AssetBrowserViewMode.Split)]
    [InlineData(AssetBrowserViewMode.Tree)]
    public void DroppedFilesImportIntoHoveredFolderWithUndo(AssetBrowserViewMode mode)
    {
        using var browser = new AssetBrowserHarness(mode);
        var source = Directory.CreateTempSubdirectory("turian-desktop-drop-");
        try
        {
            var first = Path.Combine(source.FullName, "external-a.txt");
            var second = Path.Combine(source.FullName, "external-b.txt");
            File.WriteAllText(first, "a");
            File.WriteAllText(second, "b");
            var queue = new FileDropQueue();
            browser.Gui.Platform.Register<IFileDropCapability>(queue);
            var folder = browser.Entry("Folder");
            browser.State.SetExpanded(browser.Root, true);
            browser.Frame();
            var row = browser.Field<List<TreeItem>>("rows").FindIndex(item => item.Id == folder.AbsolutePath);
            var target = mode == AssetBrowserViewMode.Grid
                ? browser.Find("assets/tile/" + folder.AbsolutePath) : browser.Find("treeview/row" + row);
            var position = target.Rect.Center;
            queue.Enqueue([first, second], position);
            browser.Input.MousePosition.Returns(new Vector2(-100));
            browser.Frame();
            var copied = Path.Combine(folder.AbsolutePath, "external-a.txt");
            Assert.True(File.Exists(copied));
            Assert.True(File.Exists(Path.Combine(folder.AbsolutePath, "external-b.txt")));
            Assert.True(File.Exists(copied + ".meta"));
            Assert.True(File.Exists(first));
            browser.Undo.Undo();
            Assert.False(File.Exists(copied));
            browser.Undo.Redo();
            Assert.True(File.Exists(copied));
        }
        finally { source.Delete(recursive: true); }
    }

    /// <summary>Desktop imports reject immutable destinations, metadata sidecars and recursive folder copies.</summary>
    [Fact]
    public void InvalidDropDestinationsAndSourcesDoNotChangeFiles()
    {
        using var browser = new AssetBrowserHarness();
        var operations = browser.Field<AssetFileOperations>("operations");
        var import = new AssetExternalImport(operations);
        var destination = browser.Entry("Folder");
        Assert.False(import.Import(destination with { IsReadOnly = true }, [browser.Entry("a.txt").AbsolutePath]));
        Assert.False(import.Import(destination, [browser.Root]));
        Assert.False(import.Import(destination, [browser.Entry("a.txt").AbsolutePath + ".meta", "relative.txt"]));
        Assert.False(import.Import(browser.Entry("a.txt"), [browser.Entry("b.txt").AbsolutePath]));
        Assert.Single(Directory.GetFiles(destination.AbsolutePath, "*.txt"));
    }

    /// <summary>A drop on empty grid space imports into the current folder rather than a neighboring tile.</summary>
    [Fact]
    public void BackgroundDropImportsIntoCurrentFolder()
    {
        using var browser = new AssetBrowserHarness();
        var source = Path.GetTempFileName();
        try
        {
            var queue = new FileDropQueue();
            browser.Gui.Platform.Register<IFileDropCapability>(queue);
            var grid = browser.Find("assets/grid").Rect;
            queue.Enqueue([source], new Vector2(grid.X + grid.W - 30, grid.Y + grid.H - 30));
            browser.Frame();
            var imported = Path.Combine(browser.Root, Path.GetFileName(source));
            Assert.True(File.Exists(imported));
            browser.Undo.Undo();
            Assert.False(File.Exists(imported));
        }
        finally { File.Delete(source); }
    }

    /// <summary>Desktop folder imports copy nested contents with fresh identities and leave their sources intact.</summary>
    [Fact]
    public void DirectoryDropCopiesNestedAssetsWithFreshIds()
    {
        using var browser = new AssetBrowserHarness();
        var source = Directory.CreateTempSubdirectory("turian-folder-drop-");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(source.FullName, "Nested"));
            var original = Path.Combine(nested.FullName, "external.txt");
            File.WriteAllText(original, "external");
            var originalId = Guid.NewGuid();
            Serializer.Save(original + ".meta", new Asset { Id = originalId });
            var destination = browser.Entry("Folder");
            var queue = new FileDropQueue();
            browser.Gui.Platform.Register<IFileDropCapability>(queue);
            queue.Enqueue([source.FullName], browser.Find("assets/tile/" + destination.AbsolutePath).Rect.Center);
            browser.Frame();
            var imported = Path.Combine(destination.AbsolutePath, source.Name, "Nested", "external.txt");
            Assert.Equal("external", File.ReadAllText(imported));
            Assert.NotEqual(originalId, Serializer.Load<Asset>(imported + ".meta")!.Id);
            browser.Undo.Undo();
            Assert.False(File.Exists(imported));
            Assert.True(File.Exists(original));
            browser.Undo.Redo();
            Assert.True(File.Exists(imported));
        }
        finally { source.Delete(recursive: true); }
    }
}
