namespace Turian.Tests;

/// <summary>
/// Checks what happens to scene tabs over their life: saving a new scene, renaming or reopening an asset, closing the
/// playing tab, switching project, and both kinds' layouts surviving a restart. Uses probe panels, not viewports, so it
/// runs without a GPU.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class SceneDocumentLifecycleTests
{
    const string ProbeId = "test.probe";

    /// <summary>A scene-bound panel that records which document it was built for and whether it was released.</summary>
    sealed class ProbePanel(IDocument document) : IPanel, IDisposable
    {
        internal IDocument Document => document;

        internal bool Disposed { get; private set; }

        public void Render(Gui gui)
        {
        }

        public void Dispose() => Disposed = true;
    }

    sealed class BlankPanel : IPanel
    {
        public void Render(Gui gui)
        {
        }
    }

    static readonly string[] GlobalIds = [GayaPlugin.InspectorPanelId, GayaPlugin.AssetsPanelId, GayaPlugin.OutputPanelId];

    /// <summary>A workbench with the three global panels, the scene and asset kinds, and a probe bound to scene tabs.</summary>
    static WorkbenchFrame NewFrame(List<ProbePanel> probes, string? layoutPath = null) => new(layoutPath: layoutPath,
        configure: app =>
        {
            app.Panels.Register(new PanelDescriptor(GayaPlugin.InspectorPanelId, "Inspector", PanelPlacement.Right,
                static _ => new BlankPanel()));
            app.Panels.Register(new PanelDescriptor(GayaPlugin.AssetsPanelId, "Assets", PanelPlacement.Bottom,
                static _ => new BlankPanel()));
            app.Panels.Register(new PanelDescriptor(GayaPlugin.OutputPanelId, "Output", PanelPlacement.Bottom,
                static _ => new BlankPanel()));
            app.DocumentKinds.Register(SceneDocument.SceneKind);
            app.DocumentKinds.Register(AssetDocument.AssetKind);
            app.Panels.Register(PanelDescriptor.ForDocuments(ProbeId, "Probe", PanelPlacement.Center,
                [SceneDocument.KindId], (_, document) => Track(probes, new ProbePanel(document))));
        });

    static ProbePanel Track(List<ProbePanel> probes, ProbePanel panel)
    {
        probes.Add(panel);
        return panel;
    }

    static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order()];

    /// <summary>Saving a new scene keeps its tab, document and panel; closing it releases the panel and the document.</summary>
    [Fact]
    public void SavingANewSceneKeepsItsTabDocumentAndPanels()
    {
        var probes = new List<ProbePanel>();
        using var frame = NewFrame(probes);
        using var harness = new SceneTabsHarness(frame.Documents);
        var tab = harness.Workspace.NewScene();
        frame.Draw();
        var probe = Assert.Single(probes);
        var id = tab.Asset!.Id.ToString();
        Assert.Equal(id, probe.Document.Id);

        harness.Workspace.Save(tab);
        frame.Draw();

        Assert.Equal(id, frame.Documents.Active!.Id);
        Assert.Equal([id], frame.Documents.Documents.Select(static document => document.Id));
        Assert.Same(probe, Assert.Single(probes));
        Assert.False(probe.Disposed);

        harness.Workspace.Close(tab);
        frame.Draw();

        Assert.True(probe.Disposed);
        Assert.Empty(frame.Documents.Documents);
        Assert.Null(frame.Documents.Active);
    }

    /// <summary>Renaming an open asset changes its title only: the document, its active state and its panel stay.</summary>
    [Fact]
    public void RenamingAnOpenTabKeepsItsDocumentAndPanels()
    {
        var probes = new List<ProbePanel>();
        using var frame = NewFrame(probes);
        using var harness = new SceneTabsHarness(frame.Documents);
        var tab = harness.Open("before", "a");
        frame.Draw();
        var document = frame.Documents.Active!;
        var probe = Assert.Single(probes);

        tab.Wrapper.Asset!.RenameTo("Assets/after.prefab");
        frame.Draw();

        Assert.Equal("after", document.Title);
        Assert.Same(document, frame.Documents.Active);
        Assert.Equal(tab.Id, frame.Documents.Active!.Id);
        Assert.Single(frame.Documents.Documents);
        Assert.Same(probe, Assert.Single(probes));
        Assert.False(probe.Disposed);
    }

    /// <summary>Opening an asset that is already open activates it again: one document and one set of panel instances.</summary>
    [Fact]
    public void OpeningAnOpenAssetAgainKeepsOneDocumentAndOnePanel()
    {
        var probes = new List<ProbePanel>();
        using var frame = NewFrame(probes);
        using var harness = new SceneTabsHarness(frame.Documents);
        var tab = harness.Open("one");
        frame.Draw();
        var document = frame.Documents.Active!;
        var probe = Assert.Single(probes);

        harness.Workspace.Open(tab.Wrapper.Asset!);
        frame.Draw();

        Assert.Single(harness.Workspace.Documents);
        Assert.Single(frame.Documents.Documents);
        Assert.Same(document, frame.Documents.Active);
        Assert.Same(probe, Assert.Single(probes));
        Assert.False(probe.Disposed);
    }

    /// <summary>
    /// Closing the playing tab while it is active hands the play session to the tab the workspace activates next, which is
    /// the tab the user sees.
    /// </summary>
    [Fact]
    public void ClosingTheActivePlayingTabMovesPlayToTheTabThatBecomesActive()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one");
        var two = harness.Open("two");
        harness.Open("three");
        harness.Activate(one);
        harness.Activate(two);
        Assert.True(harness.Play.Start());

        harness.Workspace.Close(two.Wrapper);

        Assert.True(harness.Play.IsActive);
        Assert.Equal("three", harness.Documents.Active!.Title);
        Assert.True(harness.Playing.IsPlayingIn(harness.Documents.Active.Id));
    }

    /// <summary>Opening another project closes a scene and a material tab, disposing the scene's panels and leaving the global layout.</summary>
    [Fact]
    public void ProjectSwitchDisposesEveryTabsPanelsAndReturnsToTheGlobalLayout()
    {
        var probes = new List<ProbePanel>();
        using var frame = NewFrame(probes);
        using var harness = new SceneTabsHarness(frame.Documents);
        harness.Open("one");
        frame.Draw();
        harness.Open("two");
        frame.Draw();
        harness.OpenMaterial("brick");
        frame.Draw();
        Assert.Equal(2, probes.Count);

        harness.Settings.Set(new AppSettings());
        frame.Draw();

        Assert.All(probes, static probe => Assert.True(probe.Disposed));
        Assert.Empty(frame.Documents.Documents);
        Assert.Null(frame.Documents.Active);
        Assert.Equal(Sorted(GlobalIds), Sorted(frame.Workbench.Layout.PanelIds));
        Assert.Null(frame.Workbench.Panel(ProbeId));
    }

    /// <summary>The scene layout and the asset layout both keep the panels the user hid, across a restart.</summary>
    [Fact]
    public void SceneAndAssetLayoutsBothSurviveARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"turian-kinds-{Guid.NewGuid():N}.json");
        try
        {
            var probes = new List<ProbePanel>();
            using (var frame = NewFrame(probes, path))
            using (var harness = new SceneTabsHarness(frame.Documents))
            {
                harness.Open("one");
                frame.Draw();
                frame.Workbench.TogglePanel(GayaPlugin.AssetsPanelId);
                frame.Draw();
                harness.OpenMaterial("brick");
                frame.Draw();
                frame.Workbench.TogglePanel(GayaPlugin.OutputPanelId);
                frame.Draw();
            }

            using var restarted = NewFrame(probes, path);
            using var again = new SceneTabsHarness(restarted.Documents);
            restarted.Draw();
            Assert.Equal(Sorted(GlobalIds), Sorted(restarted.Workbench.Layout.PanelIds));

            again.Open("one");
            restarted.Draw();
            Assert.Equal(Sorted([GayaPlugin.InspectorPanelId, GayaPlugin.OutputPanelId, ProbeId]),
                Sorted(restarted.Workbench.Layout.PanelIds));

            again.OpenMaterial("brick");
            restarted.Draw();
            Assert.Equal(Sorted([GayaPlugin.InspectorPanelId, GayaPlugin.AssetsPanelId]),
                Sorted(restarted.Workbench.Layout.PanelIds));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
