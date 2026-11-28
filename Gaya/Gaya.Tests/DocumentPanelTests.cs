namespace Gaya.Tests;

/// <summary>Checks panel instances per document: identity, UI state isolation, release, suspension and the close guard.</summary>
[Collection(SerialTests.Name)]
public sealed class DocumentPanelTests
{
    sealed class GuardedPanel : IPanel, IPanelCloseGuard
    {
        internal readonly List<Action<bool>> Requests = [];
        internal Action? OnRender;

        public void Render(Gui gui)
        {
            if (gui.Pass == Pass.Pass2Render)
            {
                var action = OnRender;
                OnRender = null;
                action?.Invoke();
            }
        }

        public void RequestClose(Action<bool> decide) => Requests.Add(decide);
    }

    /// <summary>Two documents of one kind get their own instances, built once and reused; global panels stay single.</summary>
    [Fact]
    public void DocumentsGetDistinctInstancesAndGlobalPanelsAreShared()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        host.Activate("a1");

        var first = host.Instance("a.view", "a1");
        var second = host.Instance("a.view", "a2");
        Assert.NotSame(first, second);
        Assert.Same(first, host.Workbench.Panel("a.view"));
        Assert.Single(host.Created, static panel => panel is { PanelId: "a.view", Document.Id: "a1" });
        Assert.Single(host.Created, static panel => panel.PanelId == "tools");
        Assert.True(first.Renders > 0 && second.Renders > 0);
    }

    /// <summary>Only the active document's panels render; an inactive document's panels stay still.</summary>
    [Fact]
    public void InactiveDocumentPanelsNeverRender()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        var inactive = host.Instance("a.view", "a1");
        var rendered = inactive.Renders;

        host.Frame.Draw();
        host.Frame.Draw();

        Assert.Equal(rendered, inactive.Renders);
        Assert.True(host.Instance("a.view", "a2").Renders > 0);
    }

    /// <summary>Scroll offsets and other per-node state of one document's panel never show up in another's.</summary>
    [Fact]
    public void DocumentPanelsDoNotShareUiState()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        var gui = host.Frame.Gui;
        var one = host.Instance("a.view", "a1").ProbeId!;
        var two = host.Instance("a.view", "a2").ProbeId!;
        Assert.NotEqual(one, two);

        gui.ScrollBy(two, new Vector2(0, 60));
        host.Frame.Draw();
        Assert.True(gui.GetScrollState(two)!.ScrollOffset.Y > 0);

        host.Activate("a1");
        Assert.Equal(one, host.Instance("a.view", "a1").ProbeId);
        Assert.Equal(0f, gui.GetScrollState(one)?.ScrollOffset.Y ?? 0f, 0.001f);

        host.Activate("a2");
        Assert.True(gui.GetScrollState(two)!.ScrollOffset.Y > 0);
    }

    /// <summary>A global panel keeps one set of node identities whichever document is active.</summary>
    [Fact]
    public void GlobalPanelsKeepTheirNodeIdentity()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        var before = host.Instance("tools", null).ProbeId;

        host.Open("a2");

        Assert.Equal(before, host.Instance("tools", null).ProbeId);
    }

    /// <summary>Closing a document disposes its panels and only its panels; the next recent document takes over.</summary>
    [Fact]
    public void ClosingADocumentDisposesItsInstances()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        host.Open("b1", DocumentWorkbench.B);

        Assert.True(host.Frame.Documents.Close("b1"));
        host.Frame.Draw();

        Assert.True(host.Instance("b.view", "b1").Disposed);
        Assert.True(host.Instance("shared", "b1").Disposed);
        Assert.False(host.Instance("a.view", "a1").Disposed);
        Assert.False(host.Instance("a.view", "a2").Disposed);
        Assert.False(host.Instance("tools", null).Disposed);
        Assert.Equal("a2", host.Frame.Documents.Active!.Id);
        Assert.Contains("a.view", host.Layout.PanelIds);
        Assert.Equal(1, host.Created.Count(static panel => panel.PanelId == "b.view"));
    }

    /// <summary>A panel that closes its own document while drawing is disposed after the frame, never under its own feet.</summary>
    [Fact]
    public void ClosingFromInsideARenderDisposesAfterTheFrame()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        var panel = host.Instance("a.view", "a2");
        panel.OnRender = () => host.Frame.Documents.Close("a2");

        host.Frame.Draw();

        Assert.True(panel.Disposed);
        Assert.False(panel.DisposedWhileRendering);
        Assert.Equal("a1", host.Frame.Documents.Active!.Id);
    }

    /// <summary>A document closed between the passes is not rebuilt by the second one.</summary>
    [Fact]
    public void ClosingBetweenPassesDoesNotRebuildPanels()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");

        host.Frame.Draw(() => host.Frame.Documents.Close("a2"));

        Assert.Single(host.Created, panel => panel is { PanelId: "a.view", Document.Id: "a2" });
        Assert.True(host.Instance("a.view", "a2").Disposed);
    }

    /// <summary>A document opened and closed between the passes of one frame never gets panels, and the other kind's layout is left intact.</summary>
    [Fact]
    public void ActivateAndCloseBetweenPassesNeverBuildTheClosedDocument()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        host.Frame.Draw();
        var built = host.Created.Count;

        host.Frame.Draw(() =>
        {
            host.Frame.Documents.Open(DocumentWorkbench.Doc("b1", DocumentWorkbench.B));
            host.Frame.Documents.Close("b1");
        });
        host.Frame.Draw();

        Assert.DoesNotContain(host.Created, static panel => panel.Document?.Id == "b1");
        Assert.Equal(built, host.Created.Count);
        Assert.Equal("a2", host.Frame.Documents.Active!.Id);
        Assert.Contains("a.view", host.Layout.PanelIds);
        Assert.DoesNotContain("b.view", host.Layout.PanelIds);
        Assert.Equal(["a.tree", "a.view", "output", "shared", "tools"], DocumentWorkbench.Ids(host.Layout));
    }

    /// <summary>The workbench stopping disposes every instance, global and per document.</summary>
    [Fact]
    public void StoppingTheWorkbenchDisposesEveryInstance()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        var created = host.Created.ToList();

        host.Frame.Dispose();

        Assert.All(created, static panel => Assert.True(panel.Disposed));
    }

    /// <summary>Panels of documents beyond the three most recently active are suspended, once, until activated again.</summary>
    [Fact]
    public void PanelsBeyondTheRecentSetAreSuspended()
    {
        using var host = new DocumentWorkbench();
        foreach (var id in new[] { "a1", "a2", "a3" })
        {
            host.Open(id);
        }

        Assert.All(host.Created, static panel => Assert.Equal(0, panel.Suspends));

        host.Open("a4");
        var oldest = host.Instance("a.view", "a1");
        Assert.Equal(1, oldest.Suspends);
        Assert.Equal(1, host.Instance("shared", "a1").Suspends);
        Assert.Equal(0, host.Instance("a.view", "a2").Suspends);

        host.Open("a5");
        Assert.Equal(1, oldest.Suspends);
        Assert.Equal(1, host.Instance("a.view", "a2").Suspends);

        host.Activate("a1");
        host.Activate("a5");
        host.Activate("a1");
        Assert.Equal(1, oldest.Suspends);
        Assert.Equal(1, host.Instance("a.view", "a3").Suspends);
        Assert.Equal(0, host.Instance("tools", null).Suspends);
    }

    /// <summary>A panel suspended again after being used again is asked again, and the limit is configurable.</summary>
    [Fact]
    public void TheRecentLimitIsConfigurable()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        Assert.Equal(3, host.Workbench.RecentDocumentLimit);

        host.Workbench.RecentDocumentLimit = 1;
        Assert.Equal(1, host.Instance("a.view", "a1").Suspends);
        Assert.Equal(0, host.Instance("a.view", "a2").Suspends);

        host.Activate("a1");
        host.Open("a3");
        host.Activate("a1");
        Assert.Equal(1, host.Instance("a.view", "a2").Suspends);
        Assert.Equal(1, host.Instance("a.view", "a3").Suspends);
        Assert.Equal(2, host.Instance("a.view", "a1").Suspends);

        host.Workbench.RecentDocumentLimit = 0;
        Assert.Equal(1, host.Workbench.RecentDocumentLimit);
    }

    /// <summary>A panel that throws while suspending is logged and does not break the switch.</summary>
    [Fact]
    public void ThrowingSuspendDoesNotBreakSwitching()
    {
        using var frame = new WorkbenchFrame(configure: static app => app.Panels.Register(PanelDescriptor.ForDocuments(
            "view", "view", PanelPlacement.Center, ["k"], static (_, _) => new ThrowingPanel())));
        frame.Documents.Open(DocumentWorkbench.Doc("d1", "k"));
        frame.Draw();
        frame.Workbench.RecentDocumentLimit = 1;

        frame.Documents.Open(DocumentWorkbench.Doc("d2", "k"));

        Assert.Equal("d2", frame.Documents.Active!.Id);
    }

    sealed class ThrowingPanel : IPanel, ISuspendablePanel
    {
        public void Render(Gui gui)
        {
        }

        public void Suspend() => throw new InvalidOperationException("boom");
    }

    /// <summary>A document panel whose factory throws shows the failure placeholder instead of taking the editor down.</summary>
    [Fact]
    public void ThrowingDocumentFactoryYieldsAPlaceholder()
    {
        using var frame = new WorkbenchFrame(configure: static app => app.Panels.Register(PanelDescriptor.ForDocuments(
            "view", "view", PanelPlacement.Center, ["k"], static (_, _) => throw new InvalidOperationException("boom"))));
        frame.Documents.Open(DocumentWorkbench.Doc("d1", "k"));

        frame.Draw();

        Assert.NotNull(frame.Workbench.Panel("view"));
    }

    /// <summary>A global panel called for a document is the same as before: its factory runs without a document.</summary>
    [Fact]
    public void DocumentPanelDescriptorDefaultsToGlobal()
    {
        var global = new PanelDescriptor("g", "g", PanelPlacement.Left, _ => new DocumentPanel("g", null));
        var bound = PanelDescriptor.ForDocuments("d", "d", PanelPlacement.Left, ["k"], (_, doc) => new DocumentPanel("d", doc));

        Assert.False(global.IsDocumentBound);
        Assert.True(global.AllowedIn(null));
        Assert.True(global.AllowedIn("k"));
        Assert.True(bound.IsDocumentBound);
        Assert.False(bound.AllowedIn(null));
        Assert.False(bound.AllowedIn("other"));
        Assert.True(bound.AllowedIn("k"));
        Assert.Throws<InvalidOperationException>(() => bound.Factory(new ServiceCollection().BuildServiceProvider()));
    }

    /// <summary>Re-registering a document panel releases every document's instance, and removing it drops it everywhere.</summary>
    [Fact]
    public void ReplacingOrRemovingADocumentPanelReleasesAllItsInstances()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Open("a2");
        var one = host.Instance("shared", "a1");
        var two = host.Instance("shared", "a2");
        host.Frame.Panels.Register(PanelDescriptor.ForDocuments("shared", "shared", PanelPlacement.Left,
            [DocumentWorkbench.A, DocumentWorkbench.B], static (_, document) => new DocumentPanel("shared", document)));
        host.Frame.Draw();
        Assert.True(one.Disposed && two.Disposed);

        host.Frame.Panels.Remove("shared");
        host.Frame.Draw();
        Assert.DoesNotContain("shared", host.Layout.PanelIds);
        Assert.True(host.Created.Where(static panel => panel.PanelId == "shared").All(static panel => panel.Disposed));

        host.Frame.Panels.Register(PanelDescriptor.ForDocuments("shared", "shared", PanelPlacement.Left,
            [DocumentWorkbench.A, DocumentWorkbench.B], static (_, document) => new DocumentPanel("shared", document)));
        host.Frame.Draw();
        Assert.Contains("shared", host.Layout.PanelIds);
    }

    /// <summary>A guarded document panel is asked before the dock's close button removes it, and the answer is kept per document.</summary>
    [Fact]
    public void CloseGuardStillDecidesForDocumentPanels()
    {
        var guards = new Dictionary<string, GuardedPanel>();
        using var frame = new WorkbenchFrame(configure: app =>
        {
            app.Panels.Register(PanelDescriptor.ForDocuments("doc", "doc", PanelPlacement.Left, ["k"],
                (_, document) => guards[document.Id] = new GuardedPanel()));
            app.Panels.Register(new PanelDescriptor("main", "main", PanelPlacement.Center, _ => new DocumentPanel("main", null)));
        });
        frame.Documents.Open(DocumentWorkbench.Doc("d1", "k"));
        frame.Draw();
        var layout = frame.Workbench.Layout;

        guards["d1"].OnRender = () => layout.Remove("doc");
        frame.Draw();
        Assert.Single(guards["d1"].Requests);
        Assert.True(layout.Contains("doc"));
        Assert.Equal(DockZone.Left, layout.FindLeaf("doc")!.Zone);

        frame.Workbench.ClosePanel("doc");
        Assert.Single(guards["d1"].Requests);

        frame.Documents.Open(DocumentWorkbench.Doc("d2", "k"));
        frame.Draw();
        frame.Workbench.ClosePanel("doc");
        Assert.Single(guards["d2"].Requests);

        guards["d2"].Requests[0](true);
        Assert.False(layout.Contains("doc"));
        frame.Documents.Close("d1");
        frame.Draw();
    }

    /// <summary>A document-bound panel without a guard closes at once through the View menu command.</summary>
    [Fact]
    public void UnguardedDocumentPanelsCloseImmediately()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");

        host.Workbench.ClosePanel("a.tree");

        Assert.DoesNotContain("a.tree", host.Layout.PanelIds);
        host.Workbench.ClosePanel("a.tree");
    }

    /// <summary>With no document kinds or documents the workbench lays out exactly the global panels, as before.</summary>
    [Fact]
    public void WithoutDocumentsTheWorkbenchBehavesAsBefore()
    {
        using var frame = new WorkbenchFrame(configure: static app =>
        {
            app.Panels.Register(new PanelDescriptor("main", "main", PanelPlacement.Center, static _ => new DocumentPanel("main", null)));
            app.Panels.Register(new PanelDescriptor("tree", "tree", PanelPlacement.Left, static _ => new DocumentPanel("tree", null)));
            app.Panels.Register(PanelDescriptor.ForDocuments("scene", "scene", PanelPlacement.Center, ["k"],
                static (_, document) => new DocumentPanel("scene", document)));
        });
        frame.Draw();

        Assert.Equal(["main", "tree"], frame.Workbench.Layout.PanelIds.Order());
        Assert.NotNull(frame.Workbench.Panel("tree"));
        Assert.Null(frame.Workbench.Panel("scene"));
        frame.Workbench.TogglePanel("tree");
        Assert.False(frame.Workbench.Layout.Contains("tree"));
        frame.Workbench.TogglePanel("tree");
        Assert.True(frame.Workbench.Layout.Contains("tree"));
    }

    /// <summary>A document that is already open when the workbench starts decides the first layout.</summary>
    [Fact]
    public void ADocumentOpenBeforeTheWorkbenchStartsIsActive()
    {
        using var frame = new WorkbenchFrame(configure: static app =>
        {
            app.Panels.Register(PanelDescriptor.ForDocuments("scene", "scene", PanelPlacement.Center, ["k"],
                static (_, document) => new DocumentPanel("scene", document)));
            app.Documents.Open(DocumentWorkbench.Doc("d1", "k"));
        });

        frame.Draw();

        Assert.Contains("scene", frame.Workbench.Layout.PanelIds);
    }
}
