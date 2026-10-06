namespace Turian.Tests;

/// <summary>Checks asset-browser modifier gestures and batch actions through the shared selection controls.</summary>
[Collection(SerialTests.Name)]
public sealed class MultiSelectionPanelTests
{
    /// <summary>Clearing, dropping and revealing a common reference field operate on every selected owner.</summary>
    [Fact]
    public void SharedReferenceActionsReachEveryOwner()
    {
        var assets = new AssetManager();
        var loader = Substitute.For<IAssetLoader>();
        var database = new AssetDatabase();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var root = new Node();
        var target = new Node { Parent = root };
        root.Children.Add(target);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        var first = new ObjectReferencesTests.Linker { Target = target };
        var second = new ObjectReferencesTests.Linker { Target = new Node() };
        var field = InspectorForms.BuildForObjects([first, second]).Sections[0].Fields
            .Single(member => member.Name == nameof(ObjectReferencesTests.Linker.Target));
        var reference = ReferenceField.TryCreate(field)!;
        Assert.False(reference.IsReadOnly);
        var selection = new NodeInspectorController(assets);
        var drawer = new ReferenceDrawer(new ReferencePicker(database, tree, loader), selection, new AssetRevealService());
        var handle = typeof(ReferenceDrawer).GetMethod("HandleResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Act(ObjectFieldResult result) => handle.Invoke(drawer, [result, reference, "shared"]);
        Act(new ObjectFieldResult(ObjectFieldAction.Clear));
        Assert.Null(first.Target);
        Assert.Null(second.Target);
        Act(new ObjectFieldResult(ObjectFieldAction.Pick));
        Assert.Equal("shared", typeof(ReferenceDrawer)
            .GetField("openFieldId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(drawer));
        Act(new ObjectFieldResult(ObjectFieldAction.Pick));
        Assert.Null(typeof(ReferenceDrawer).GetField("openFieldId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(drawer));
        Act(new ObjectFieldResult(ObjectFieldAction.Drop, new ReferenceDragPayload(target.Id, target.Name)));
        Assert.Same(target, first.Target);
        Assert.Same(target, second.Target);
        Act(new ObjectFieldResult(ObjectFieldAction.Reveal));
        Assert.Same(target, selection.SelectedNode);
    }

    /// <summary>Multi-asset import forms apply and revert every target while a locked panel preserves external selection.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiAssetInspectorAppliesAndRevertsEveryTarget(bool locked)
    {
        var project = Path.Combine(Path.GetTempPath(), $"turian-inspector-selection-{Guid.NewGuid():N}");
        var folder = Path.Combine(project, "Assets");
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new SettingsService();
            settings.Set(new AppSettings { ProjectAbsoluteDir = project });
            var database = new AssetDatabase();
            var logger = Substitute.For<ILogger>();
            using var importer = new AssetImporter(logger, database, settings);
            importer.StopMonitoring();
            var loader = Substitute.For<IAssetLoader>();
            var inspections = new AssetInspectionService(importer, new AssetTypeCatalog(), settings, loader, logger);
            var assets = new AssetManager();
            var selection = new NodeInspectorController(assets);
            var tree = new SceneTreeController(assets, settings, importer, loader,
                Substitute.For<ISceneManager>(), database);
            using var undo = new UndoService(tree, selection, assets, loader);
            var entries = Enumerable.Range(0, 2).Select(index => new AssetInspection(
                new DataAssetAsset { Id = Guid.NewGuid(), RelativePath = $"Assets/{index}.bin" },
                Path.Combine(folder, $"{index}.bin"), new ObjectReferencesTests.Stats { Health = 10 + index },
                "Import Settings", IsPayload: false)).ToArray();
            foreach (var entry in entries) File.WriteAllText(entry.AbsolutePath, "content");
            selection.SelectMany(entries);
            using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!), null!,
                inspections, new InspectorSettings(), null!, null!, undo,
                new AssetAutoSave(inspections), null!, null!, database);
            panel.Locked = locked;
            var external = new Node();
            if (locked) selection.Select(external);
            var input = Substitute.For<IInputHandler>();
            input.MousePosition.Returns(new Vector2(-1));
            var gui = new Gui { Input = input };
            using var surface = SKSurface.Create(new SKImageInfo(640, 800));
            var font = Font.FromFamilyName("sans-serif", 14);
            void Frame()
            {
                gui.SetStage(Pass.Pass1Build);
                gui.BeginFrame(surface.Canvas, font, font);
                panel.Render(gui);
                gui.CalculateLayout();
                gui.SetStage(Pass.Pass2Render);
                panel.Render(gui);
                gui.Render();
                gui.EndFrame();
            }
            Frame();
            typeof(InspectorPanel).GetMethod("OnSelectedAssetEdited", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, [entries[0].Target]);
            typeof(InspectorPanel).GetField("applyRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, true);
            Frame();
            Assert.All(entries, entry => Assert.True(File.Exists($"{entry.AbsolutePath}.meta")));
            typeof(InspectorPanel).GetField("revertRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, true);
            Frame();
            Frame();
            if (locked) Assert.Same(external, selection.SelectedNode);
            else Assert.Equal(entries.Select(entry => entry.AbsolutePath),
                selection.Selection.Objects.Cast<AssetInspection>().Select(entry => entry.AbsolutePath));
        }
        finally { Directory.Delete(project, recursive: true); }
    }

    /// <summary>Selected assets travel together, duplicate together and delete in one undoable step.</summary>
    [Fact]
    public void BrowserSelectionDragsDuplicatesAndDeletesTogether()
    {
        var project = Path.Combine(Path.GetTempPath(), $"turian-browser-selection-{Guid.NewGuid():N}");
        var folder = Path.Combine(project, "Assets");
        Directory.CreateDirectory(folder);
        try
        {
            var paths = new[] { "a.bin", "b.bin", "c.bin" }.Select(name => Path.Combine(folder, name)).ToArray();
            foreach (var path in paths) File.WriteAllText(path, "content");
            foreach (var path in paths) Serializer.Save($"{path}.meta", new DataAssetAsset
            {
                Id = Guid.NewGuid(),
                RelativePath = Path.GetRelativePath(project, path),
            });
            var settings = new SettingsService();
            var app = new AppSettings { ProjectAbsoluteDir = project };
            settings.Set(app);
            var assets = new AssetManager();
            var loader = Substitute.For<IAssetLoader>();
            var database = new AssetDatabase();
            var logger = Substitute.For<ILogger>();
            using var build = new BuildManager(app, logger);
            using var importer = new AssetImporter(logger, database, settings, build);
            importer.StopMonitoring();
            var files = new AssetFileSystem(settings, importer);
            var types = new AssetTypeCatalog();
            var selection = new NodeInspectorController(assets);
            var tree = new SceneTreeController(assets, settings, importer, loader,
                Substitute.For<ISceneManager>(), database);
            using var undo = new UndoService(tree, selection, assets, loader);
            var operations = new AssetFileOperations(files, undo);
            var reveal = new AssetRevealService();
            var confirm = new ConfirmDialogChrome(new StudioLocalization());
            var panel = new AssetBrowserPanel(files, settings, null!,
                new AssetInspectionService(importer, types, settings, loader, logger), selection,
                reveal, new AssetCreationCatalog(files, settings, build, logger),
                new AssetBrowserSettings(), Substitute.For<IEditorSettings>(), types,
                new AssetPreviewCatalog(build), null!, operations,
                new BricksController(settings, null!, null!, logger), confirm, build);
            var state = (TreeViewState)typeof(AssetBrowserPanel)
                .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
            state.SetExpanded(folder, true);
            var input = Substitute.For<IInputHandler>();
            input.MousePosition.Returns(new Vector2(-1));
            var gui = new Gui { Input = input };
            gui.Platform.Register(Substitute.For<IClipboard>());
            using var surface = SKSurface.Create(new SKImageInfo(640, 800));
            var font = Font.FromFamilyName("sans-serif", 14);
            void Frame()
            {
                gui.SetStage(Pass.Pass1Build);
                gui.BeginFrame(surface.Canvas, font, font);
                panel.Render(gui);
                gui.CalculateLayout();
                gui.SetStage(Pass.Pass2Render);
                panel.Render(gui);
                gui.Render();
                gui.EndFrame();
            }
            Frame();
            Frame();
            void ClickRow(int row)
            {
                input.MousePosition.Returns(new Vector2(200, row * 20 + 10));
                input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
                input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
                Frame();
                input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
                input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
                Frame();
            }
            ClickRow(1);
            input.IsKeyDown(KeyboardKey.LeftShift).Returns(true);
            ClickRow(2);
            input.IsKeyDown(KeyboardKey.LeftShift).Returns(false);
            Assert.Equal(paths.Take(2), state.SelectedIds);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Right).Returns(true);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Right).Returns(false);
            Frame();
            Assert.Equal(paths.Take(2), state.SelectedIds);
            var entry = new AssetEntry(paths[0], false, null, folder);
            var item = new TreeItem(paths[0], "a", 1, Tag: entry);
            var payload = (ReferenceDragPayload)typeof(AssetBrowserPanel)
                .GetMethod("DragPayload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [item])!;
            Assert.Equal(paths.Take(2), payload.Entries.Select(asset => asset.AbsolutePath));
            var destination = Path.Combine(folder, "Moved");
            Directory.CreateDirectory(destination);
            typeof(AssetBrowserPanel).GetMethod("OnDrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, [new TreeItem(destination, "Moved", 1,
                    Tag: new AssetEntry(destination, true, null, folder)), payload]);
            Assert.All(paths.Take(2), path => Assert.True(File.Exists(Path.Combine(destination, Path.GetFileName(path)))));
            Assert.Single(undo.History.UndoSteps);
            undo.Undo();
            Assert.All(paths, path => Assert.True(File.Exists(path)));
            panel.DuplicateSelected();
            Assert.Equal(5, Directory.GetFiles(folder, "*.bin").Length);
            Assert.Single(undo.History.UndoSteps);
            undo.Undo();
            Assert.Equal(3, Directory.GetFiles(folder, "*.bin").Length);
            Assert.Equal(paths.Take(2), state.SelectedIds);
            panel.DeleteSelected();
            Assert.All(paths.Take(2), path => Assert.False(File.Exists(path)));
            Assert.True(File.Exists(paths[2]));
            Assert.Empty(state.SelectedIds);
            Assert.Single(undo.History.UndoSteps);
            undo.Undo();
            Assert.All(paths, path => Assert.True(File.Exists(path)));
            var entries = (IReadOnlyList<AssetEntry>)typeof(AssetBrowserPanel)
                .GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
            var source = entries.Single(asset => asset.AbsolutePath == paths[0]);
            reveal.Reveal(source.AssetMetadata!.Id);
            Frame();
            Assert.Equal([paths[0]], state.SelectedIds);
            var rows = (List<TreeItem>)typeof(AssetBrowserPanel)
                .GetField("rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
            var rowIndex = rows.FindIndex(row => row.Id == paths[0]);
            var readOnly = source with { IsReadOnly = true };
            rows[rowIndex] = rows[rowIndex] with { Tag = readOnly };
            var brickPayload = new ReferenceDragPayload(source.AssetMetadata.Id, "a", paths[0])
            {
                Entries = [readOnly],
            };
            var drop = typeof(AssetBrowserPanel).GetMethod("OnDrop", BindingFlags.Instance | BindingFlags.NonPublic)!;
            drop.Invoke(panel, [new TreeItem(folder, "Assets", 0, Tag: new AssetEntry(folder, true, null, null)),
                brickPayload]);
            Assert.Equal(3, Directory.GetFiles(folder, "*.bin").Length);
            var continuation = (Action)typeof(ConfirmDialogChrome)
                .GetField("confirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(confirm)!;
            continuation();
            Assert.Equal(4, Directory.GetFiles(folder, "*.bin").Length);
            Assert.True(File.Exists(paths[0]));
        }
        finally { Directory.Delete(project, recursive: true); }
    }
}
