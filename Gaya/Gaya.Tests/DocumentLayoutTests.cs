namespace Gaya.Tests;

/// <summary>Checks the per-document-kind layouts: what each holds, how they swap, persist and reset.</summary>
[Collection(SerialTests.Name)]
public sealed class DocumentLayoutTests
{
    static readonly string[] Everywhere = ["output", "tools"];

    /// <summary>Panels bound to a kind appear in that kind's layout only, next to every global panel.</summary>
    [Fact]
    public void KindPanelsShowOnlyForTheirKind()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        Assert.Equal(["a.tree", "a.view", "output", "shared", "tools"], DocumentWorkbench.Ids(host.Layout));

        host.Open("b1", DocumentWorkbench.B);
        Assert.Equal(["b.view", "output", "shared", "tools"], DocumentWorkbench.Ids(host.Layout));
        Assert.DoesNotContain(host.Created, static panel => panel.PanelId == "a.view" && panel.Document?.Id == "b1");
        Assert.DoesNotContain(host.Created, static panel => panel.PanelId == "b.view" && panel.Document?.Id == "a1");
        Assert.Contains(host.Created, static panel => panel.PanelId == "b.view" && panel.Document?.Id == "b1");
    }

    /// <summary>Each kind keeps its own arrangement while the user switches between kinds.</summary>
    [Fact]
    public void EachKindsArrangementSurvivesSwitching()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        var aLayout = host.Layout;
        aLayout.DockInto("a.tree", aLayout.FindLeaf("a.view")!, DockZone.Center);

        host.Open("b1", DocumentWorkbench.B);
        Assert.NotSame(aLayout, host.Layout);
        var bLayout = host.Layout;
        Assert.DoesNotContain("a.tree", bLayout.PanelIds);
        Assert.NotEqual(bLayout.FindLeaf("shared"), bLayout.FindLeaf("b.view"));
        bLayout.DockInto("shared", bLayout.FindLeaf("b.view")!, DockZone.Center);

        host.Activate("a1");
        Assert.Same(aLayout, host.Layout);
        Assert.Contains("a.tree", aLayout.FindLeaf("a.view")!.PanelIds);
        Assert.DoesNotContain("shared", aLayout.FindLeaf("a.view")!.PanelIds);

        host.Activate("b1");
        Assert.Same(bLayout, host.Layout);
        Assert.Contains("shared", bLayout.FindLeaf("b.view")!.PanelIds);
    }

    /// <summary>Switching between documents of one kind keeps the layout and only rebinds the panels.</summary>
    [Fact]
    public void SameKindSwitchKeepsTheLayoutAndRebindsInstances()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        var layout = host.Layout;
        layout.DockInto("a.tree", layout.FindLeaf("a.view")!, DockZone.Center);
        host.Open("a2");

        Assert.Same(layout, host.Layout);
        Assert.Contains("a.tree", layout.FindLeaf("a.view")!.PanelIds);
        Assert.Equal("a2", ((DocumentPanel)host.Workbench.Panel("a.view")!).Document!.Id);
        host.Activate("a1");
        Assert.Equal("a1", ((DocumentPanel)host.Workbench.Panel("a.view")!).Document!.Id);
    }

    /// <summary>With no document open the layout holds the global panels only, and no document panel is built.</summary>
    [Fact]
    public void NoDocumentShowsGlobalPanelsOnly()
    {
        using var host = new DocumentWorkbench();
        host.Frame.Draw();

        Assert.Equal(Everywhere, DocumentWorkbench.Ids(host.Layout));
        Assert.All(host.Created, static panel => Assert.Null(panel.Document));
        Assert.Null(host.Workbench.Panel("a.view"));

        host.Open("a1");
        host.Frame.Documents.Deactivate();
        host.Frame.Draw();
        Assert.Equal(Everywhere, DocumentWorkbench.Ids(host.Layout));
    }

    /// <summary>Every kind's arrangement, and the no-document one, comes back after a restart.</summary>
    [Fact]
    public void LayoutsSurviveARestart()
    {
        using var host = new DocumentWorkbench();
        host.Frame.Draw();
        host.Layout.DockInto("output", host.Layout.FindLeaf("tools")!, DockZone.Center);
        host.Open("a1");
        host.Layout.DockInto("a.tree", host.Layout.FindLeaf("a.view")!, DockZone.Center);
        host.Open("b1", DocumentWorkbench.B);
        host.Layout.DockInto("shared", host.Layout.FindLeaf("b.view")!, DockZone.Center);
        host.Workbench.TogglePanel("tools");

        host.Restart();
        host.Frame.Draw();
        Assert.Equal(["output", "tools"], host.Layout.FindLeaf("tools")!.PanelIds.Order());

        host.Open("a1");
        Assert.Contains("a.tree", host.Layout.FindLeaf("a.view")!.PanelIds);
        Assert.Contains("tools", host.Layout.PanelIds);
        Assert.DoesNotContain("shared", host.Layout.FindLeaf("a.view")!.PanelIds);

        host.Open("b1", DocumentWorkbench.B);
        Assert.Contains("shared", host.Layout.FindLeaf("b.view")!.PanelIds);
        Assert.DoesNotContain("tools", host.Layout.PanelIds);
    }

    /// <summary>The outgoing layout is written when the kind changes, not only when the workbench stops.</summary>
    [Fact]
    public void SwitchingKindSavesTheOutgoingLayout()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Layout.DockInto("a.tree", host.Layout.FindLeaf("a.view")!, DockZone.Center);
        Assert.False(File.Exists(host.LayoutPath));

        host.Open("b1", DocumentWorkbench.B);

        Assert.True(File.Exists(host.LayoutPath));
        Assert.Contains("a.tree", File.ReadAllText(host.LayoutPath));
    }

    /// <summary>A global panel may join any kind's layout; a document panel only the kinds it is bound to.</summary>
    [Fact]
    public void PlacementRuleHoldsForTogglingAndShowing()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");

        host.Workbench.TogglePanel("b.view");
        host.Workbench.ShowPanel("b.view");
        Assert.DoesNotContain("b.view", host.Layout.PanelIds);

        host.Workbench.TogglePanel("shared");
        Assert.DoesNotContain("shared", host.Layout.PanelIds);
        host.Workbench.TogglePanel("shared");
        Assert.Contains("shared", host.Layout.PanelIds);

        host.Open("b1", DocumentWorkbench.B);
        host.Workbench.ShowPanel("a.tree");
        Assert.DoesNotContain("a.tree", host.Layout.PanelIds);

        host.Workbench.TogglePanel("tools");
        Assert.DoesNotContain("tools", host.Layout.PanelIds);
        host.Workbench.TogglePanel("tools");
        Assert.Contains("tools", host.Layout.PanelIds);
    }

    /// <summary>Closing a panel hides it in the active kind's layout and leaves the other kinds alone.</summary>
    [Fact]
    public void ClosingAPanelHidesItInTheActiveKindOnly()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        var aLayout = host.Layout;
        host.Open("b1", DocumentWorkbench.B);
        var bLayout = host.Layout;

        host.Workbench.ClosePanel("shared");
        Assert.DoesNotContain("shared", bLayout.PanelIds);
        Assert.Contains("shared", aLayout.PanelIds);

        host.Activate("a1");
        host.Workbench.ClosePanel("tools");
        Assert.DoesNotContain("tools", aLayout.PanelIds);
        Assert.Contains("tools", bLayout.PanelIds);
    }

    /// <summary>The View menu lists the panels the active kind allows and nothing else.</summary>
    [Theory]
    [InlineData(DocumentWorkbench.A, "a.tree,a.view,output,shared,tools")]
    [InlineData(DocumentWorkbench.B, "b.view,output,shared,tools")]
    [InlineData("", "output,tools")]
    public void ViewMenuListsOnlyAllowedPanels(string kind, string expected)
    {
        using var host = new DocumentWorkbench();
        if (kind.Length > 0)
        {
            host.Open("d1", kind);
        }

        Assert.Equal(expected.Split(','), ViewMenuPanels(host));
    }

    static string[] ViewMenuPanels(DocumentWorkbench host)
    {
        var rows = new List<ControlSemantics>();
        var sink = Substitute.For<IControlSemanticsSink>();
        sink.When(s => s.Publish(Arg.Any<ControlSemantics>())).Do(call => rows.Add(call.Arg<ControlSemantics>()));
        var frame = host.Frame;
        frame.Gui.ControlSemantics = sink;
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Input.MousePosition.Returns(new Vector2(-1, -1));
        frame.Draw();
        var menu = WorkbenchFrame.Descendants(frame.Gui.RootNode!).Single(node => node.Id == "menubar");
        frame.Input.MousePosition.Returns(menu.Rect.Position + new Vector2(15, 15));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Draw();
        var view = rows.First(static row => row.Label == "View" && row.Role == ControlRole.Menu);
        frame.Input.MousePosition.Returns(view.Bounds.Position + new Vector2(5, 5));
        frame.Draw();
        rows.Clear();
        frame.Draw();

        var labels = rows.Where(row => row.Id.StartsWith("/menubar/View/v0/i", StringComparison.Ordinal))
            .Select(row => row.Label ?? "").ToList();
        frame.Input.MousePosition.Returns(new Vector2(-1, -1));
        frame.Draw();
        string[] panels = ["a.tree", "a.view", "b.view", "output", "shared", "tools"];
        return [.. labels.Where(panels.Contains).Order()];
    }

    /// <summary>Reset Layout restores only the active kind's arrangement; Reset All Layouts restores every one.</summary>
    [Fact]
    public void ResetRestoresTheActiveKindOrEveryKind()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        var aLayout = host.Layout;
        aLayout.DockInto("a.tree", aLayout.FindLeaf("a.view")!, DockZone.Center);
        host.Workbench.TogglePanel("tools");
        host.Open("b1", DocumentWorkbench.B);
        var bLayout = host.Layout;
        bLayout.DockInto("shared", bLayout.FindLeaf("b.view")!, DockZone.Center);

        host.Workbench.ExecuteCommand(ShellCommands.ResetLayout);
        Assert.NotEqual(bLayout.FindLeaf("shared"), bLayout.FindLeaf("b.view"));
        Assert.DoesNotContain("tools", aLayout.PanelIds);
        Assert.Contains("a.tree", aLayout.FindLeaf("a.view")!.PanelIds);

        host.Workbench.ExecuteCommand(ShellCommands.ResetAllLayouts);
        Assert.Contains("tools", aLayout.PanelIds);
        Assert.NotEqual(aLayout.FindLeaf("a.tree"), aLayout.FindLeaf("a.view"));
        Assert.Same(bLayout, host.Layout);
        Assert.NotEqual(bLayout.FindLeaf("shared"), bLayout.FindLeaf("b.view"));

        host.Restart();
        host.Open("a1");
        Assert.Contains("tools", host.Layout.PanelIds);
        Assert.NotEqual(host.Layout.FindLeaf("a.tree"), host.Layout.FindLeaf("a.view"));
    }

    /// <summary>The reset commands are registered and offered under the View menu.</summary>
    [Fact]
    public void ResetCommandsAreInTheViewMenu()
    {
        using var host = new DocumentWorkbench();
        var items = host.Frame.Menus.ItemsFor(MenuIds.View).Select(static item => item.CommandId).ToList();

        Assert.Contains(ShellCommands.ResetLayout, items);
        Assert.Contains(ShellCommands.ResetAllLayouts, items);
        Assert.True(host.Workbench.CanExecuteCommand(ShellCommands.ResetLayout));
    }

    /// <summary>A kind seeds its layout at the placements it declares instead of the panels' own.</summary>
    [Fact]
    public void KindPlacementsOverrideDefaultPlacements()
    {
        using var frame = new WorkbenchFrame(configure: static app =>
        {
            app.DocumentKinds.Register(new DocumentKind("k.custom", "Custom")
            {
                Placements = new Dictionary<string, PanelPlacement> { ["tools"] = PanelPlacement.Floating },
            });
            app.Panels.Register(new PanelDescriptor("main", "main", PanelPlacement.Center, static _ => new DocumentPanel("main", null)));
            app.Panels.Register(new PanelDescriptor("tools", "tools", PanelPlacement.Right, static _ => new DocumentPanel("tools", null)));
        });

        Assert.Empty(frame.Workbench.Layout.Floating);
        frame.Documents.Open(DocumentWorkbench.Doc("c1", "k.custom"));
        Assert.Single(frame.Workbench.Layout.Floating);
        frame.Workbench.ResetLayout();
        Assert.Single(frame.Workbench.Layout.Floating);
        frame.Documents.Deactivate();
        Assert.Empty(frame.Workbench.Layout.Floating);
    }
}
