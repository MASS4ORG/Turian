namespace Turian.Tests;

/// <summary>
/// Covers how the workbench turns contributed panels into a dock layout, and how it reconciles a
/// layout saved by an earlier run against the panels a later run actually registers.
/// </summary>
public class GayaWorkbenchLayoutTests : IDisposable
{
    readonly string layoutPath = Path.Combine(Path.GetTempPath(),
        $"gaya-layout-{Guid.NewGuid():N}.json");

    readonly List<GayaApplication> applications = [];

    /// <summary>Disposes the applications the test created and deletes its layout file.</summary>
    public void Dispose()
    {
        foreach (var application in applications) application.Dispose();
        if (File.Exists(layoutPath)) File.Delete(layoutPath);
        GC.SuppressFinalize(this);
    }

    sealed class StubPanel : IPanel
    {
        public void Render(Gui gui)
        {
        }
    }

    static PanelDescriptor Panel(string id, PanelPlacement placement, bool openByDefault = true) =>
        new(id, id, placement, _ => new StubPanel()) { OpenByDefault = openByDefault };

    GayaApplication Application(params (string Id, PanelPlacement Placement)[] panels) =>
        Application([.. panels.Select(panel => Panel(panel.Id, panel.Placement))]);

    GayaApplication Application(params PanelDescriptor[] panels)
    {
        var registry = new PanelRegistry();
        foreach (var descriptor in panels)
            registry.Register(descriptor);

        var services = new ServiceCollection();
        services.AddSingleton<ILogger>(NullLogger.Instance);

        var application = new GayaApplication(services.BuildServiceProvider(), registry, new CommandRegistry(),
            new MenuRegistry(), new ChromeRegistry(), new TabStripChromeRegistry(),
            new ShortcutService(NullLogger.Instance), new FocusTracker(), []);
        applications.Add(application);
        return application;
    }

    Workbench NewWorkbench(GayaApplication app) =>
        new(app, null, new WorkbenchLayoutStore(NullLogger.Instance, layoutPath));

    /// <summary>Draws one headless frame, which is when the workbench picks up registry changes.</summary>
    static void RenderFrame(Workbench workbench)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        input.PrevMousePosition.Returns(new Vector2(-1, -1));

        var gui = new Gui { Input = input };
        var font = Font.FromFamilyName("sans-serif", 14);
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        workbench.Render(gui);
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        workbench.Render(gui);
        gui.Render();
        gui.EndFrame();
    }

    /// <summary>A fresh layout holds every registered panel, whatever its placement.</summary>
    [Fact]
    public void SeedingPlacesEveryContributedPanelInTheLayout()
    {
        using var workbench = NewWorkbench(Application(
            ("centre", PanelPlacement.Center),
            ("tree", PanelPlacement.Left),
            ("inspector", PanelPlacement.Right),
            ("output", PanelPlacement.Bottom)));

        Assert.Equal(["centre", "inspector", "output", "tree"], workbench.Layout.PanelIds.Order());
    }

    /// <summary>Two Left panels become one column of tabs, not two nested columns.</summary>
    [Fact]
    public void PanelsSharingAPlacementShareOneTabGroup()
    {
        using var workbench = NewWorkbench(Application(
            ("centre", PanelPlacement.Center),
            ("tree", PanelPlacement.Left),
            ("assets", PanelPlacement.Left)));

        var left = workbench.Layout.FindLeaf("tree");
        Assert.NotNull(left);
        Assert.Equal(["tree", "assets"], left.PanelIds);
    }

    /// <summary>PanelPlacement.Floating produces a floating window rather than being dropped.</summary>
    [Fact]
    public void AFloatingPlacementBecomesAFloatingWindow()
    {
        using var workbench = NewWorkbench(Application(
            ("centre", PanelPlacement.Center),
            ("notes", PanelPlacement.Floating)));

        Assert.Single(workbench.Layout.Floating);
        Assert.True(workbench.Layout.Contains("notes"));
    }

    /// <summary>An arrangement the user made is still there the next time the workbench starts.</summary>
    [Fact]
    public void TheLayoutSurvivesARestart()
    {
        var app = Application(("centre", PanelPlacement.Center), ("tree", PanelPlacement.Left));

        using (var first = NewWorkbench(app))
        {
            first.Layout.DockInto("tree", first.Layout.FindLeaf("centre")!, DockZone.Center);
        }

        Assert.True(File.Exists(layoutPath));

        using var second = NewWorkbench(app);
        var leaf = second.Layout.FindLeaf("centre");
        Assert.NotNull(leaf);
        Assert.Equal(["centre", "tree"], leaf.PanelIds);
    }

    /// <summary>A saved layout outliving its plugin loses that plugin’s panels instead of showing blanks.</summary>
    [Fact]
    public void ARestoredLayoutDropsPanelsNoPluginContributesAnyMore()
    {
        using (var first = NewWorkbench(Application(
                   ("centre", PanelPlacement.Center), ("gone", PanelPlacement.Left))))
        {
            first.Layout.MarkChanged();
        }

        using var second = NewWorkbench(Application(("centre", PanelPlacement.Center)));

        Assert.False(second.Layout.Contains("gone"));
        Assert.True(second.Layout.Contains("centre"));
    }

    /// <summary>A panel registered after the layout was saved still appears, at its declared placement.</summary>
    [Fact]
    public void ARestoredLayoutFoldsInPanelsThatAreNewSinceItWasSaved()
    {
        using (var first = NewWorkbench(Application(("centre", PanelPlacement.Center))))
        {
            first.Layout.MarkChanged();
        }

        using var second = NewWorkbench(Application(
            ("centre", PanelPlacement.Center), ("added", PanelPlacement.Right)));

        Assert.True(second.Layout.Contains("added"));
    }

    /// <summary>The View menu's panel entry closes an open panel and brings a closed one back.</summary>
    [Fact]
    public void TogglingAPanelClosesAndReopensIt()
    {
        using var workbench = NewWorkbench(Application(
            ("centre", PanelPlacement.Center), ("tree", PanelPlacement.Left)));

        Assert.True(workbench.Layout.Contains("tree"));

        workbench.TogglePanel("tree");
        Assert.False(workbench.Layout.Contains("tree"));

        workbench.TogglePanel("tree");
        Assert.True(workbench.Layout.Contains("tree"));
    }

    /// <summary>A corrupt layout file re-seeds from the registry instead of taking the editor down.</summary>
    [Fact]
    public void AnUnreadableLayoutFileIsIgnoredRatherThanFatal()
    {
        File.WriteAllText(layoutPath, "{ not json at all");

        using var workbench = NewWorkbench(Application(("centre", PanelPlacement.Center)));

        Assert.True(workbench.Layout.Contains("centre"));
    }

    /// <summary>A panel reached through a command is registered without being opened, until asked for.</summary>
    [Fact]
    public void AnOnDemandPanelOpensOnlyWhenShown()
    {
        using var workbench = NewWorkbench(Application(
            Panel("centre", PanelPlacement.Center), Panel("settings", PanelPlacement.Center, openByDefault: false)));

        Assert.False(workbench.Layout.Contains("settings"));

        workbench.ShowPanel("settings");
        Assert.True(workbench.Layout.Contains("settings"));
    }

    /// <summary>Closing a panel is remembered: registering it again on the next run does not reopen it.</summary>
    [Fact]
    public void AClosedPanelStaysClosedAfterARestart()
    {
        var app = Application(("centre", PanelPlacement.Center), ("tree", PanelPlacement.Left));

        using (var first = NewWorkbench(app))
        {
            first.TogglePanel("tree");
        }

        using var second = NewWorkbench(app);
        Assert.False(second.Layout.Contains("tree"));
        Assert.True(second.Layout.Contains("centre"));
    }

    /// <summary>An on-demand panel the user left open is open again on the next run.</summary>
    [Fact]
    public void AnOnDemandPanelLeftOpenIsRestored()
    {
        var app = Application(
            Panel("centre", PanelPlacement.Center), Panel("settings", PanelPlacement.Center, openByDefault: false));

        using (var first = NewWorkbench(app))
        {
            first.ShowPanel("settings");
        }

        using var second = NewWorkbench(app);
        Assert.True(second.Layout.Contains("settings"));
    }

    /// <summary>
    /// A panel that registers after the layout is restored — a user assembly still loading — goes back to
    /// the floating window it was saved in rather than being dropped.
    /// </summary>
    [Fact]
    public void APanelRegisteredLateReturnsToItsFloatingWindow()
    {
        var userPanel = Panel("user", PanelPlacement.Floating, openByDefault: false);
        var bounds = new Rect(400, 300, 200, 100);

        using (var first = NewWorkbench(Application(Panel("centre", PanelPlacement.Center), userPanel)))
        {
            first.ShowPanel("user");
            first.Layout.Floating.Single().Bounds = bounds;
            first.Layout.MarkChanged();
        }

        var app = Application(Panel("centre", PanelPlacement.Center));
        using var second = NewWorkbench(app);
        Assert.False(second.Layout.Contains("user"));

        app.Panels.Register(userPanel);
        RenderFrame(second);

        Assert.True(second.Layout.Contains("user"));
        Assert.Equal(bounds, second.Layout.Floating.Single().Bounds);
    }

    /// <summary>A panel unregistered and registered again, as a recompile does, keeps its tab group.</summary>
    [Fact]
    public void AReregisteredPanelKeepsItsTabGroup()
    {
        var extra = Panel("extra", PanelPlacement.Left);
        var app = Application(Panel("centre", PanelPlacement.Center), Panel("tree", PanelPlacement.Left), extra);
        using var workbench = NewWorkbench(app);
        workbench.Layout.DockInto("extra", workbench.Layout.FindLeaf("centre")!, DockZone.Center);

        app.Panels.Remove("extra");
        RenderFrame(workbench);
        Assert.False(workbench.Layout.Contains("extra"));

        app.Panels.Register(extra);
        RenderFrame(workbench);

        Assert.Equal(["centre", "extra"], workbench.Layout.FindLeaf("centre")!.PanelIds);
    }

    /// <summary>
    /// A layout saved before closed panels were remembered keeps what opens by default and loses the
    /// on-demand panels it was forced to hold.
    /// </summary>
    [Fact]
    public void ALegacyLayoutDropsOnDemandPanels()
    {
        var legacy = new DockLayout();
        legacy.DockAtEdge("centre", DockZone.Center);
        legacy.DockAtEdge("settings", DockZone.Center);
        legacy.Float("user", new Rect(10, 10, 100, 100));
        File.WriteAllText(layoutPath, legacy.ToJson());

        using var workbench = NewWorkbench(Application(
            Panel("centre", PanelPlacement.Center), Panel("settings", PanelPlacement.Center, openByDefault: false)));

        Assert.Equal(["centre"], workbench.Layout.PanelIds);
    }
}
