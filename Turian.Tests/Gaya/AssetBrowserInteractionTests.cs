namespace Turian.Tests;

/// <summary>Checks browser gestures and file edits through both GUI passes.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetBrowserInteractionTests
{
    /// <summary>The import menu copies supported files with undo and typed browser payloads remain compatible.</summary>
    [Fact]
    public void ImportDialogCopiesFilesAndRecognizesDragPayloads()
    {
        using var browser = new AssetBrowserHarness();
        browser.Invoke("ImportFile", Path.Combine(browser.Root, "Other"));
        var dialog = (FileDialogState)typeof(FileDialogChrome)
            .GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(browser.Dialog)!;
        var request = Assert.IsType<FileDialogRequest>(dialog.Request);
        Assert.NotNull(request.Validate!("unknown.unsupported"));
        request.OnComplete!(null);
        request.OnComplete!(browser.Entry("a.txt").AbsolutePath);
        Assert.True(File.Exists(Path.Combine(browser.Root, "Other", "a.txt")));
        browser.Undo.Undo();
        Assert.False(File.Exists(Path.Combine(browser.Root, "Other", "a.txt")));
        var accepts = typeof(AssetBrowserPanel).GetMethod("BrowserPayload", BindingFlags.NonPublic | BindingFlags.Static)!;
        var payload = AssetDragPayloads.Create(browser.Entry("a.txt"), [], []);
        Assert.True((bool)accepts.Invoke(null, [payload])!);
        Assert.False((bool)accepts.Invoke(null, [new object()])!);
    }

    /// <summary>Search Escape clears results and opening a folder shares breadcrumb navigation.</summary>
    [Fact]
    public void SearchEscapeAndFolderActivationReturnToFolderContents()
    {
        using var browser = new AssetBrowserHarness();
        browser.Search("deep");
        browser.Panel.FocusSearch();
        browser.Frame();
        browser.Input.IsKeyPressed(GKey.Escape).Returns(true);
        browser.Frame();
        browser.Input.IsKeyPressed(GKey.Escape).Returns(false);
        browser.Frame();
        Assert.Equal("", browser.Field<string>("search"));
        browser.Invoke("Activate", browser.Entry("Folder"));
        browser.Frame();
        Assert.Single(browser.Field<IReadOnlyList<AssetEntry>>("gridEntries"));
        Assert.Equal(Path.Combine(browser.Root, "Folder"), browser.Field<AssetBrowserNavigation>("navigation").Current);
    }

    /// <summary>Malformed metadata prevents the entire label batch from changing valid assets.</summary>
    [Fact]
    public void InvalidMetadataDoesNotPartiallyApplyLabels()
    {
        using var browser = new AssetBrowserHarness();
        var first = browser.Entry("a.txt");
        var second = browser.Entry("b.txt");
        var original = File.ReadAllText(first.AbsolutePath + ".meta");
        File.WriteAllText(second.AbsolutePath + ".meta", "{");
        Assert.False(browser.Labels.Set([first, second], "hero", true));
        Assert.Equal(original, File.ReadAllText(first.AbsolutePath + ".meta"));
        Assert.Empty(first.AssetMetadata!.Labels);
        var command = AssetOpenService.FileManagerCommand(first.AbsolutePath);
        Assert.Contains(OperatingSystem.IsMacOS() ? first.AbsolutePath : browser.Root, command.ArgumentList);
    }

    /// <summary>Tile modifier clicks preserve multi-selection and use the existing reference payload.</summary>
    [Fact]
    public void TilesSelectDragAndMoveTogether()
    {
        using var browser = new AssetBrowserHarness();
        var first = browser.Entry("a.txt");
        var second = browser.Entry("b.txt");
        browser.Click("assets/tile/" + first.AbsolutePath);
        browser.Input.IsKeyDown(GKey.LeftShift).Returns(true);
        browser.Click("assets/tile/" + second.AbsolutePath);
        browser.Input.IsKeyDown(GKey.LeftShift).Returns(false);
        Assert.Equal([first.AbsolutePath, second.AbsolutePath], browser.State.SelectedIds);
        browser.Click("assets/tile/" + first.AbsolutePath, GMouseButton.Right);
        Assert.Equal(2, browser.State.SelectedIds.Count);
        browser.Set("menuOpen", false);
        var payload = Assert.IsType<ReferenceDragPayload>(browser.Invoke("DragPayload",
            new TreeItem(first.AbsolutePath, "a", 0, Tag: first)));
        Assert.Equal(2, payload.Entries.Count);
        browser.Invoke("OnDrop", new TreeItem("Folder", "Folder", 0, Tag: browser.Entry("Folder")), payload);
        Assert.False(File.Exists(first.AbsolutePath));
        Assert.True(File.Exists(Path.Combine(browser.Root, "Folder", "a.txt")));
        browser.Undo.Undo();
        Assert.True(File.Exists(first.AbsolutePath));
        browser.Frame();
        browser.Input.IsKeyDown(GKey.LeftControl).Returns(true);
        browser.Click("assets/tile/" + second.AbsolutePath);
        browser.Input.IsKeyDown(GKey.LeftControl).Returns(false);
        Assert.Equal([first.AbsolutePath], browser.State.SelectedIds);
    }

    /// <summary>Search spans the project; clearing it restores the folder and reveals navigate to the asset.</summary>
    [Theory]
    [InlineData(AssetBrowserViewMode.Tree)]
    [InlineData(AssetBrowserViewMode.Split)]
    [InlineData(AssetBrowserViewMode.Grid)]
    public void SearchBreadcrumbsAndRevealShareNavigation(AssetBrowserViewMode mode)
    {
        using var browser = new AssetBrowserHarness(mode);
        browser.Search("deep");
        var deep = browser.Entry(Path.Combine("Folder", "deep.txt"));
        Assert.Single(browser.Field<IReadOnlyList<AssetEntry>>("gridEntries"));
        Assert.NotNull(browser.Find("assets/tile/" + deep.AbsolutePath));
        browser.Panel.ClearFilters();
        browser.Frame();
        browser.Gui.ClearFocus();
        browser.Reveal.Reveal(deep.AssetMetadata!.Id);
        browser.Frame();
        Assert.Equal(Path.Combine(browser.Root, "Folder"), browser.Field<AssetBrowserNavigation>("navigation").Current);
        Assert.Equal(deep.AbsolutePath, browser.State.SelectedId);
        browser.Click("breadcrumb/0");
        Assert.Equal(browser.Root, browser.Field<AssetBrowserNavigation>("navigation").Current);
        browser.Click("assets/back");
        Assert.Equal(Path.Combine(browser.Root, "Folder"), browser.Field<AssetBrowserNavigation>("navigation").Current);
        browser.Click("assets/up");
        Assert.Equal(browser.Root, browser.Field<AssetBrowserNavigation>("navigation").Current);
    }

    /// <summary>Ctrl-wheel changes zoom without scrolling, including at the start and end of a large folder.</summary>
    [Fact]
    public void WheelZoomDoesNotScrollAndSurvivesRescan()
    {
        using var browser = new AssetBrowserHarness(width: 400, height: 180);
        browser.Input.MousePosition.Returns(browser.Find("assets/grid").Rect.Center);
        var scroll = browser.Gui.GetScrollState("assets/grid")!;
        browser.Input.IsKeyDown(GKey.LeftControl).Returns(true);
        browser.Input.MouseWheelDelta.Returns(-1);
        var offset = scroll.ScrollOffset;
        browser.Frame();
        Assert.Equal(48, browser.Preferences.GridZoom);
        Assert.Equal(offset, scroll.ScrollOffset);
        browser.Input.MouseWheelDelta.Returns(0);
        browser.Input.IsKeyDown(GKey.LeftControl).Returns(false);
        browser.Panel.Rescan(browser.Root);
        browser.Frame();
        Assert.Equal(48, browser.Preferences.GridZoom);
        browser.Panel.SelectAll();
        Assert.Equal(4, browser.State.SelectedIds.Count);
    }

    /// <summary>Context edits preserve metadata, labels persist and batch copy/cut paste are undoable.</summary>
    [Fact]
    public void LabelsFavoritesAndClipboardSurviveEdits()
    {
        using var browser = new AssetBrowserHarness();
        var first = browser.Entry("a.txt");
        var second = browser.Entry("b.txt");
        browser.State.SetSelection([first.AbsolutePath, second.AbsolutePath]);
        Assert.True(browser.Labels.Set([first, second], " hero ", true));
        browser.Frame();
        Assert.Contains("hero", Asset.Load(first.AbsolutePath + ".meta")!.Labels);
        Assert.Contains("hero", browser.Database.Assets[first.AssetMetadata!.Id].Labels);
        browser.Undo.Undo();
        Assert.Empty(Asset.Load(first.AbsolutePath + ".meta")!.Labels);
        browser.Undo.Redo();
        browser.Frame();
        Assert.Contains("hero", browser.Entry("a.txt").AssetMetadata!.Labels);
        browser.Invoke("SetFavorites", new[] { first }, true);
        browser.Invoke("Rename", new TreeItem(first.AbsolutePath, "a", 0, Tag: first), "renamed.txt");
        browser.Frame();
        var renamed = Path.Combine(browser.Root, "renamed.txt");
        Assert.Contains(renamed, browser.Field<AssetFavoritesStore>("favorites").Paths);
        browser.Undo.Undo();
        browser.Frame();
        Assert.Contains(first.AbsolutePath, browser.Field<AssetFavoritesStore>("favorites").Paths);
        browser.State.SetSelection([first.AbsolutePath, second.AbsolutePath]);
        browser.Panel.CopySelected();
        browser.State.SelectedId = Path.Combine(browser.Root, "Other");
        browser.Panel.PasteSelected();
        Assert.True(File.Exists(Path.Combine(browser.Root, "Other", "a.txt")));
        browser.Undo.Undo();
        Assert.False(File.Exists(Path.Combine(browser.Root, "Other", "a.txt")));
        browser.Undo.Redo();
        browser.Frame();
        browser.State.SetSelection([first.AbsolutePath, second.AbsolutePath]);
        browser.Panel.CutSelected();
        browser.State.SelectedId = Path.Combine(browser.Root, "Folder");
        browser.Panel.PasteSelected();
        Assert.False(File.Exists(first.AbsolutePath));
        browser.Undo.Undo();
        Assert.True(File.Exists(first.AbsolutePath));
        browser.Frame();
        browser.State.SelectedId = first.AbsolutePath;
        browser.Panel.CopySelected();
        browser.State.SelectedId = Path.Combine(browser.Root, "Folder");
        browser.Panel.PasteSelected();
        Assert.True(File.Exists(Path.Combine(browser.Root, "Folder", "a.txt")));
    }

    /// <summary>Rename fields commit and cancel safely, keyboard arrows select tiles and metadata menus copy values.</summary>
    [Fact]
    public void RenameKeyboardAndMenuActionsReachTheSelection()
    {
        using var browser = new AssetBrowserHarness();
        var entry = browser.Entry("a.txt");
        browser.Click("assets/tile/" + entry.AbsolutePath);
        browser.Panel.RenameSelected();
        browser.Frame();
        browser.State.EditingText = "named";
        browser.Input.IsKeyPressed(GKey.Enter).Returns(true);
        browser.Frame();
        browser.Input.IsKeyPressed(GKey.Enter).Returns(false);
        browser.Frame();
        var renamed = browser.Entry("named.txt");
        Assert.Equal(entry.AssetMetadata!.Id, renamed.AssetMetadata!.Id);
        browser.State.SelectedId = renamed.AbsolutePath;
        browser.Panel.RenameSelected();
        browser.Frame();
        browser.Input.IsKeyPressed(GKey.Escape).Returns(true);
        browser.Frame();
        browser.Input.IsKeyPressed(GKey.Escape).Returns(false);
        Assert.Null(browser.State.EditingId);
        browser.Gui.ClearFocus();
        browser.Panel.CopySelectedPath(false);
        browser.Panel.CopySelectedPath(true);
        browser.Clipboard.Received().SetClipboardText(renamed.AbsolutePath);
        browser.Clipboard.Received().SetClipboardText("Assets/named.txt");
        browser.Invoke("RevealEntry", renamed);
        browser.Invoke("Reimport", (object)new[] { renamed });
        browser.Frame();
        browser.Gui.RequestFocus("assets/grid");
        foreach (var key in new[] { GKey.Left, GKey.Right, GKey.Up, GKey.Down })
        {
            browser.Input.IsKeyPressed(key).Returns(true);
            browser.Frame();
            browser.Input.IsKeyPressed(key).Returns(false);
            browser.Frame();
        }
        Assert.NotNull(browser.State.SelectedId);
        browser.Panel.RenameSelected();
        Assert.True(browser.Panel.HasSelection);
        browser.Invoke("OnEmptyClick", GMouseButton.Right);
        browser.Frame();
        browser.Set("menuOpen", false);
        browser.Panel.ClearFilters();
        Assert.False(browser.Labels.Set([], "label", true));
        Assert.False(browser.Labels.Set([renamed], " ", true));
        Assert.False(browser.Labels.Set([renamed with { IsReadOnly = true }], "label", true));
        Assert.True(browser.Labels.Set([renamed], "label", false));
    }

    /// <summary>Favorites shortcuts, layout cycling and settings persist through the existing settings store.</summary>
    [Fact]
    public void FavoritesAndViewPreferencesArePersisted()
    {
        using var browser = new AssetBrowserHarness();
        browser.Invoke("SetFavorites", new[] { browser.Entry("a.txt") }, true);
        browser.Preferences.ShowFavoritesInTree = true;
        browser.Preferences.ViewMode = AssetBrowserViewMode.Tree;
        browser.Invoke("OnEditorSettingsChanged");
        browser.Frame();
        browser.Frame();
        browser.Click("treeview/row0");
        Assert.True(browser.Field<bool>("favoritesOnly"));
        browser.Panel.ClearFilters();
        browser.Click("assets/view");
        Assert.Equal(AssetBrowserViewMode.Split, browser.Preferences.ViewMode);
        var settingsPath = Path.Combine(browser.Project, "preferences.json");
        var store = new EditorSettings(NullLogger.Instance, settingsPath);
        store.Register(new SettingsPageDescriptor(AssetBrowserSettings.PageId, "Asset Browser", browser.Preferences));
        browser.Preferences.GridZoom = 160;
        store.NotifyChanged(AssetBrowserSettings.PageId);
        store.Save();
        var restored = new AssetBrowserSettings();
        new EditorSettings(NullLogger.Instance, settingsPath).Register(
            new SettingsPageDescriptor(AssetBrowserSettings.PageId, "Asset Browser", restored));
        Assert.Equal(160, restored.GridZoom);
        Assert.Equal(AssetBrowserViewMode.Split, restored.ViewMode);
        Assert.True(restored.ShowFavoritesInTree);
        Assert.Contains(browser.Entry("a.txt").AbsolutePath, new AssetFavoritesStore(browser.Project).Paths);
    }

    /// <summary>The browser's overflow menu builds preferences and responds to pointer activation.</summary>
    [Fact]
    public void ChromePreferencesRenderAndEditWithoutAWindow()
    {
        using var browser = new AssetBrowserHarness();
        var store = Substitute.For<IEditorSettings>();
        var chrome = new AssetBrowserChrome(browser.Preferences, store, Substitute.For<IShellHost>());
        browser.Frame(chrome.Render);
        var button = browser.Find("gaya.turian.assets/tabMenu");
        browser.Input.MousePosition.Returns(button.Rect.Center);
        browser.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        browser.Frame(chrome.Render);
        browser.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        browser.Frame(chrome.Render);
        browser.Frame(chrome.Render);
        var preferences = typeof(AssetBrowserChrome).GetMethod("Preferences", BindingFlags.NonPublic | BindingFlags.Static)!;
        void Draw(Gui gui) => preferences.Invoke(null, [gui, browser.Preferences, store]);
        browser.Frame(Draw);
        browser.Input.MousePosition.Returns(new Vector2(10, 10));
        browser.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        browser.Frame(Draw);
        browser.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        browser.Frame(Draw);
        store.Received().NotifyChanged(AssetBrowserSettings.PageId);
    }

    /// <summary>Writable, mixed, brick and empty selections get explicit menu rules.</summary>
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(1, true, false)]
    public void ContextMenuRulesCoverSelectionKinds(int count, bool readOnly, bool writable)
    {
        var folder = new AssetEntry("/Assets", true, null, null);
        var selection = Enumerable.Range(0, count).Select(index =>
            new AssetEntry($"/Assets/{index}.txt", false, new Asset(), "/Assets", readOnly)).ToArray();
        var rules = AssetContextMenu.Rules(selection, folder, true, _ => true);
        Assert.Equal(writable, rules.Writable);
        Assert.Equal(count == 1 && writable, rules.CanRename);
        Assert.Equal(writable, rules.CanLabel);
        Assert.Equal(writable, rules.CanReimport);
        Assert.True(rules.CanPaste);
        Assert.True(rules.CanCreate);
        Assert.False(AssetContextMenu.Rules(selection, folder with { IsReadOnly = true }, true, _ => true).CanPaste);
    }
}
