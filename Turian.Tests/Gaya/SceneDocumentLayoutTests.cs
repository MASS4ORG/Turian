namespace Turian.Tests;

/// <summary>Checks the Studio's scene layout: where its panels dock, what the no-document state shows and what closing a tab releases.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneDocumentLayoutTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    sealed class EmptyPanel : IPanel
    {
        public void Render(Gui gui)
        {
        }
    }

    static readonly string[] GlobalIds = [GayaPlugin.InspectorPanelId, GayaPlugin.AssetsPanelId, GayaPlugin.OutputPanelId];
    static readonly string[] SceneIds =
        [GayaPlugin.ViewportPanelId, GayaPlugin.GamePanelId, GayaPlugin.SceneTreePanelId];

    static void RegisterGlobals(GayaApplication app)
    {
        app.Panels.Register(new PanelDescriptor(GayaPlugin.InspectorPanelId, "Inspector", PanelPlacement.Right,
            static _ => new EmptyPanel()));
        app.Panels.Register(new PanelDescriptor(GayaPlugin.AssetsPanelId, "Assets", PanelPlacement.Bottom,
            static _ => new EmptyPanel()));
        app.Panels.Register(new PanelDescriptor(GayaPlugin.OutputPanelId, "Output", PanelPlacement.Bottom,
            static _ => new EmptyPanel()));
    }

    /// <summary>Registers the plugin's real scene kind and panels; their factories need services the harness lacks.</summary>
    static void RegisterScenePanels(GayaApplication app)
    {
        var context = Substitute.For<IPluginContext>();
        context.Panels.Returns(app.Panels);
        context.DocumentKinds.Returns(app.DocumentKinds);
        SceneDocumentPanels.Register(context);
    }

    static SceneDocument NewTab(string name) =>
        new(new AssetWrapper(new Prefab { Id = Guid.NewGuid(), RelativePath = $"Assets/{name}.prefab" }));

    static string[] Ids(DockLayout layout) => Sorted(layout.PanelIds);

    static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order()];

    static string Legacy()
    {
        var layout = new DockLayout();
        layout.DockAtEdge(GayaPlugin.ViewportPanelId, DockZone.Center);
        layout.DockInto(GayaPlugin.GamePanelId, layout.FindLeaf(GayaPlugin.ViewportPanelId)!, DockZone.Center);
        layout.DockAtEdge(GayaPlugin.SceneTreePanelId, DockZone.Left);
        layout.DockAtEdge(GayaPlugin.InspectorPanelId, DockZone.Right);
        layout.DockAtEdge(GayaPlugin.AssetsPanelId, DockZone.Bottom);
        layout.DockInto(GayaPlugin.OutputPanelId, layout.FindLeaf(GayaPlugin.AssetsPanelId)!, DockZone.Center);
        return new JsonObject
        {
            ["layout"] = JsonNode.Parse(layout.ToJson()),
            ["known"] = new JsonArray([.. SceneIds.Concat(GlobalIds).Select(static id => (JsonNode?)id)]),
            ["parked"] = new JsonObject(),
        }.ToJsonString();
    }

    /// <summary>Without a document the workbench shows the global panels only, and asks no scene panel for an instance.</summary>
    [Fact]
    public void NoDocumentShowsOnlyGlobalPanels()
    {
        using var frame = new WorkbenchFrame(configure: static app =>
        {
            RegisterGlobals(app);
            RegisterScenePanels(app);
        });
        frame.Draw();

        Assert.Equal(Sorted(GlobalIds), Ids(frame.Workbench.Layout));
        Assert.All(SceneIds, id => Assert.Null(frame.Workbench.Panel(id)));
        Assert.NotNull(frame.Workbench.Panel(GayaPlugin.InspectorPanelId));

        frame.Documents.Open(NewTab("one"));
        frame.Draw();
        Assert.Equal(Sorted(GlobalIds.Concat(SceneIds)), Ids(frame.Workbench.Layout));

        frame.Documents.Deactivate();
        frame.Draw();
        Assert.Equal(Sorted(GlobalIds), Ids(frame.Workbench.Layout));
    }

    /// <summary>A fresh install docks Scene and Game as tabs of the center and the Scene Tree on the left of every scene tab.</summary>
    [Fact]
    public void SceneLayoutDocksSceneGameAndTreeLikeTheStudioAlwaysDid()
    {
        using var frame = new WorkbenchFrame(configure: static app =>
        {
            RegisterGlobals(app);
            RegisterScenePanels(app);
        });
        frame.Documents.Open(NewTab("one"));
        frame.Draw();

        var layout = frame.Workbench.Layout;
        Assert.Equal(layout.FindLeaf(GayaPlugin.ViewportPanelId), layout.FindLeaf(GayaPlugin.GamePanelId));
        Assert.Equal(DockZone.Left, layout.FindLeaf(GayaPlugin.SceneTreePanelId)!.Zone);
        Assert.Equal(DockZone.Right, layout.FindLeaf(GayaPlugin.InspectorPanelId)!.Zone);

        frame.Documents.Open(NewTab("two"));
        frame.Draw();
        Assert.Same(layout, frame.Workbench.Layout);
    }

    /// <summary>An existing layout.json keeps its arrangement in the scene layout and loses the scene panels elsewhere.</summary>
    [Fact]
    public void ExistingLayoutFileMigratesWithTurianPanelIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"turian-layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, Legacy());
        try
        {
            using var frame = new WorkbenchFrame(layoutPath: path, configure: static app =>
            {
                RegisterGlobals(app);
                RegisterScenePanels(app);
            });
            frame.Draw();
            Assert.Equal(Sorted(GlobalIds), Ids(frame.Workbench.Layout));
            Assert.Equal(DockZone.Right, frame.Workbench.Layout.FindLeaf(GayaPlugin.InspectorPanelId)!.Zone);

            frame.Documents.Open(NewTab("one"));
            frame.Draw();
            var layout = frame.Workbench.Layout;
            Assert.Equal(Sorted(GlobalIds.Concat(SceneIds)), Ids(layout));
            Assert.Equal([GayaPlugin.GamePanelId, GayaPlugin.ViewportPanelId],
                Sorted(layout.FindLeaf(GayaPlugin.ViewportPanelId)!.PanelIds));
            Assert.Equal(DockZone.Left, layout.FindLeaf(GayaPlugin.SceneTreePanelId)!.Zone);
            Assert.Equal(DockZone.Right, layout.FindLeaf(GayaPlugin.InspectorPanelId)!.Zone);
            Assert.Equal([GayaPlugin.AssetsPanelId, GayaPlugin.OutputPanelId],
                Sorted(layout.FindLeaf(GayaPlugin.AssetsPanelId)!.PanelIds));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The scene kind registers the three scene panels as bound to it and nothing else.</summary>
    [Fact]
    public void SceneKindOwnsSceneTreeSceneAndGame()
    {
        using var frame = new WorkbenchFrame(configure: RegisterScenePanels);

        var kind = frame.DocumentKinds.Find(SceneDocument.KindId)!;
        Assert.Equal(Sorted(SceneIds), Sorted(kind.Placements.Keys));
        Assert.Equal(PanelPlacement.Left, kind.Placements[GayaPlugin.SceneTreePanelId]);
        Assert.Equal(Sorted(SceneIds), Sorted(frame.Panels.All.Where(static panel => panel.IsDocumentBound)
            .Select(static panel => panel.Id)));
        Assert.All(frame.Panels.All, static panel => Assert.Contains(SceneDocument.KindId, panel.DocumentKinds));
    }

    /// <summary>A material tab shows global panels only; the scene tab returns with its panels, camera and selection.</summary>
    [Fact]
    public void AssetTabHidesScenePanelsAndTheSceneTabRestoresThem()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        SceneTabsHarness? harness = null;
        SceneViewport? view = null;
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var frame = new WorkbenchFrame(configure: app =>
        {
            RegisterGlobals(app);
            harness = new SceneTabsHarness(app.Documents);
            app.DocumentKinds.Register(SceneDocument.SceneKind);
            app.DocumentKinds.Register(AssetDocument.AssetKind);
            app.Panels.Register(PanelDescriptor.ForDocuments(GayaPlugin.ViewportPanelId, "Scene", PanelPlacement.Center,
                [SceneDocument.KindId], (_, document) =>
                {
                    view = new SceneViewport(fixture.Vulkan, harness.Database, harness.Tree, harness.Inspector,
                        new GizmoDrawerCatalog(build), harness.Play, new EditorCameraSettings(), NullLogger.Instance,
                        harness.Undo, new LocaleService(),
                        isActive: () => ReferenceEquals(app.Documents.Active, document));
                    return new ScenePanel(view, harness.Tree, harness.Inspector, (SceneDocument)document,
                        new PrefabStage(harness.Workspace, harness.Database));
                }));
        });
        using var owned = harness!;
        var scene = harness!.Open("one", "a");
        harness.Inspector.Select(harness.Node(scene, "a"));
        frame.Draw();
        var camera = (SceneViewerService)typeof(SceneViewport)
            .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        camera.Camera.Position = new Vector3(7, 8, 9);

        harness.OpenMaterial("brick");
        frame.Draw();
        Assert.Equal(Sorted(GlobalIds), Ids(frame.Workbench.Layout));
        Assert.Null(frame.Workbench.Panel(GayaPlugin.ViewportPanelId));

        harness.Activate(scene);
        frame.Draw();
        Assert.Contains(GayaPlugin.ViewportPanelId, frame.Workbench.Layout.PanelIds);
        Assert.NotNull(frame.Workbench.Panel(GayaPlugin.ViewportPanelId));
        Assert.Equal(new Vector3(7, 8, 9), camera.Camera.Position);
        Assert.Same(harness.Node(scene, "a"), harness.Inspector.SelectedNode);
    }

    /// <summary>Closing a scene tab releases its Scene view's render target, and the other tab keeps its own.</summary>
    [Fact]
    public void ClosingATabReleasesItsViewport()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        SceneTabsHarness? harness = null;
        var viewports = new Dictionary<string, SceneViewport>();
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var frame = new WorkbenchFrame(configure: app =>
        {
            RegisterGlobals(app);
            harness = new SceneTabsHarness(app.Documents);
            app.DocumentKinds.Register(SceneDocument.SceneKind);
            app.Panels.Register(PanelDescriptor.ForDocuments(GayaPlugin.ViewportPanelId, "Scene", PanelPlacement.Center,
                [SceneDocument.KindId], (_, document) =>
                {
                    var viewport = new SceneViewport(fixture.Vulkan, harness.Database, harness.Tree, harness.Inspector,
                        new GizmoDrawerCatalog(build), harness.Play, new EditorCameraSettings(), NullLogger.Instance,
                        harness.Undo, new LocaleService(),
                        isActive: () => ReferenceEquals(app.Documents.Active, document));
                    viewports[document.Id] = viewport;
                    return new ScenePanel(viewport, harness.Tree, harness.Inspector, (SceneDocument)document,
                        new PrefabStage(harness.Workspace, harness.Database));
                }));
        });
        using var owned = harness!;
        var one = harness!.Open("one");
        frame.Draw();
        var two = harness.Open("two");
        frame.Draw();
        Assert.True(viewports[one.Id].HasRenderTarget);
        Assert.True(viewports[two.Id].HasRenderTarget);
        Assert.NotSame(viewports[one.Id], viewports[two.Id]);

        harness.Workspace.Close(one.Wrapper);
        frame.Draw();

        Assert.False(viewports[one.Id].HasRenderTarget);
        Assert.True(viewports[two.Id].HasRenderTarget);
    }
}
