namespace Gaya.Tests;

/// <summary>A panel that records what the workbench does to it, so tests can see instances, rendering and release.</summary>
sealed class DocumentPanel(string panelId, IDocument? document) : IPanel, IDisposable, ISuspendablePanel
{
    internal string PanelId => panelId;
    internal IDocument? Document => document;
    internal int Renders;
    internal int Suspends;
    internal bool Disposed;
    internal bool DisposedWhileRendering;
    internal string? ProbeId;
    internal Action? OnRender;

    public void Render(Gui gui)
    {
        Renders++;
        using (gui.Node(100, 40, "probe").Direction(Axis.Vertical).Enter())
        {
            gui.ScrollY();
            ProbeId = gui.CurrentNode.Id;
            using (gui.Node(100, 400).Enter())
            {
            }
        }

        if (gui.Pass == Pass.Pass2Render)
        {
            var action = OnRender;
            OnRender = null;
            action?.Invoke();
            DisposedWhileRendering |= Disposed;
        }
    }

    public void Suspend() => Suspends++;

    public void Dispose() => Disposed = true;
}

/// <summary>
/// A headless workbench with two fake document kinds (<see cref="A"/> and <see cref="B"/>), global panels and panels
/// bound to one or both kinds, that can restart over the same layout file.
/// </summary>
sealed class DocumentWorkbench : IDisposable
{
    internal const string A = "k.a";
    internal const string B = "k.b";

    readonly string layoutPath = Path.Combine(Path.GetTempPath(), $"gaya-documents-{Guid.NewGuid():N}.json");
    internal DocumentWorkbench() => Frame = Boot();

    internal WorkbenchFrame Frame { get; private set; }
    internal Workbench Workbench => Frame.Workbench;
    internal DockLayout Layout => Workbench.Layout;
    internal string LayoutPath => layoutPath;
    internal List<DocumentPanel> Created { get; } = [];

    /// <summary>Stops the workbench, saving its layouts, and starts a new one over the same file.</summary>
    /// <param name="editFile">Changes the layout file while no workbench is running.</param>
    internal void Restart(Action? editFile = null)
    {
        Frame.Dispose();
        editFile?.Invoke();
        Created.Clear();
        Frame = Boot();
    }

    internal static DocumentInfo Doc(string id, string kind = A) => new(id, kind, id);

    internal void Open(string id, string kind = A)
    {
        Frame.Documents.Open(Doc(id, kind));
        Frame.Draw();
    }

    internal void Activate(string id)
    {
        Frame.Documents.Activate(id);
        Frame.Draw();
    }

    internal DocumentPanel Instance(string panelId, string? documentId) =>
        Created.Single(panel => panel.PanelId == panelId && panel.Document?.Id == documentId);

    internal static string[] Ids(DockLayout layout) => [.. layout.PanelIds.Order()];

    WorkbenchFrame Boot() => new(layoutPath: layoutPath, configure: Contribute);

    void Contribute(GayaApplication app)
    {
        app.DocumentKinds.Register(new DocumentKind(A, "A"));
        app.DocumentKinds.Register(new DocumentKind(B, "B"));
        Global(app, "tools", PanelPlacement.Right);
        Global(app, "output", PanelPlacement.Bottom);
        Bound(app, "a.view", PanelPlacement.Center, A);
        Bound(app, "a.tree", PanelPlacement.Left, A);
        Bound(app, "b.view", PanelPlacement.Center, B);
        Bound(app, "shared", PanelPlacement.Left, A, B);
    }

    void Global(GayaApplication app, string id, PanelPlacement placement) =>
        app.Panels.Register(new PanelDescriptor(id, id, placement, _ => Track(new DocumentPanel(id, null))));

    void Bound(GayaApplication app, string id, PanelPlacement placement, params string[] kinds) =>
        app.Panels.Register(PanelDescriptor.ForDocuments(id, id, placement, kinds,
            (_, document) => Track(new DocumentPanel(id, document))));

    DocumentPanel Track(DocumentPanel panel)
    {
        Created.Add(panel);
        return panel;
    }

    public void Dispose()
    {
        Frame.Dispose();
        if (File.Exists(layoutPath))
        {
            File.Delete(layoutPath);
        }
    }
}
