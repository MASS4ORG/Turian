namespace Gaya.Tests;

/// <summary>Checks the layout file: legacy migration, unreadable entries, unknown kinds and the document service itself.</summary>
[Collection(SerialTests.Name)]
public sealed class DocumentPersistenceTests
{
    static string Legacy(params string[] panels)
    {
        var layout = new DockLayout();
        layout.DockAtEdge(panels[0], DockZone.Center);
        foreach (var panel in panels.Skip(1))
        {
            layout.DockAtEdge(panel, DockZone.Right);
        }

        return layout.ToJson();
    }

    static string LegacyFile(string layoutJson, params string[] known) =>
        new JsonObject
        {
            ["layout"] = JsonNode.Parse(layoutJson),
            ["known"] = new JsonArray([.. known.Select(static id => (JsonNode?)id)]),
            ["parked"] = new JsonObject(),
        }.ToJsonString();

    /// <summary>Copies the saved layout of kind A under a kind no plugin registers.</summary>
    static Action AddUnknownKind(DocumentWorkbench host) => () =>
    {
        var saved = (JsonObject)JsonNode.Parse(File.ReadAllText(host.LayoutPath))!;
        var layouts = (JsonObject)saved["layouts"]!;
        layouts["k.gone"] = JsonNode.Parse(layouts["k.a"]!.ToJsonString());
        File.WriteAllText(host.LayoutPath, saved.ToJsonString());
    };

    /// <summary>A single-layout file from an earlier version seeds every kind, keeping only the panels each may hold.</summary>
    [Fact]
    public void LegacyLayoutSeedsEveryKindFilteredToAllowedPanels()
    {
        using var host = new DocumentWorkbench();
        host.Restart(() => File.WriteAllText(host.LayoutPath, LegacyFile(Legacy("a.view", "b.view", "tools"),
            "a.view", "b.view", "tools", "output", "a.tree", "shared")));
        host.Frame.Draw();

        Assert.Equal(["tools"], DocumentWorkbench.Ids(host.Layout));

        host.Open("a1");
        Assert.Equal(["a.view", "tools"], DocumentWorkbench.Ids(host.Layout));

        host.Open("b1", DocumentWorkbench.B);
        Assert.Equal(["b.view", "tools"], DocumentWorkbench.Ids(host.Layout));
    }

    /// <summary>Panels of a kind that the legacy file never saw open at their default placement.</summary>
    [Fact]
    public void LegacyLayoutPlacesMissingKindPanelsAtTheirDefaults()
    {
        using var host = new DocumentWorkbench();
        host.Restart(() => File.WriteAllText(host.LayoutPath, LegacyFile(Legacy("a.view", "tools"), "a.view", "tools")));

        host.Open("a1");

        Assert.Equal(["a.tree", "a.view", "output", "shared", "tools"], DocumentWorkbench.Ids(host.Layout));
        Assert.Equal(DockZone.Left, host.Layout.FindLeaf("a.tree")!.Zone);
    }

    /// <summary>A panel the user closed under the legacy layout stays closed in every kind.</summary>
    [Fact]
    public void LegacyClosedPanelsStayClosedEverywhere()
    {
        using var host = new DocumentWorkbench();
        host.Restart(() => File.WriteAllText(host.LayoutPath, LegacyFile(Legacy("a.view", "tools"),
            "a.view", "tools", "output", "a.tree", "shared", "b.view")));

        host.Open("a1");
        Assert.Equal(["a.view", "tools"], DocumentWorkbench.Ids(host.Layout));

        host.Open("b1", DocumentWorkbench.B);
        Assert.Equal(["tools"], DocumentWorkbench.Ids(host.Layout));
    }

    /// <summary>A file that holds a bare dock tree, older still, keeps only what opens by default.</summary>
    [Fact]
    public void BareDockTreeIsMigratedToo()
    {
        using var host = new DocumentWorkbench();
        host.Restart(() => File.WriteAllText(host.LayoutPath, Legacy("a.view", "tools")));

        host.Open("a1");

        Assert.Equal(["a.tree", "a.view", "output", "shared", "tools"], DocumentWorkbench.Ids(host.Layout));
    }

    /// <summary>A migrated layout is written in the current format on the next change and read back from it.</summary>
    [Fact]
    public void MigratedLayoutIsSavedInTheCurrentFormat()
    {
        using var host = new DocumentWorkbench();
        host.Restart(() => File.WriteAllText(host.LayoutPath, LegacyFile(Legacy("a.view", "tools"), "a.view", "tools")));
        host.Open("a1");
        host.Layout.MarkChanged();
        host.Frame.Draw();

        var saved = (JsonObject)JsonNode.Parse(File.ReadAllText(host.LayoutPath))!;
        Assert.Equal(WorkbenchLayoutStore.FormatVersion, (int)saved["version"]!);
        var kinds = ((JsonObject)saved["layouts"]!).Select(static entry => entry.Key).Order(StringComparer.Ordinal);
        Assert.Equal([WorkbenchLayoutStore.NoDocumentKey, "k.a", "k.b"], kinds);
    }

    /// <summary>An entry that cannot be read is ignored while the other layouts survive.</summary>
    [Fact]
    public void CorruptEntriesAreIgnoredSafely()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Layout.DockInto("a.tree", host.Layout.FindLeaf("a.view")!, DockZone.Center);
        host.Open("b1", DocumentWorkbench.B);
        host.Layout.DockInto("shared", host.Layout.FindLeaf("b.view")!, DockZone.Center);
        host.Restart(() =>
        {
            var saved = (JsonObject)JsonNode.Parse(File.ReadAllText(host.LayoutPath))!;
            var layouts = (JsonObject)saved["layouts"]!;
            layouts["k.a"] = "garbage";
            layouts["bad"] = new JsonObject { ["layout"] = "nope" };
            layouts["worse"] = new JsonObject { ["layout"] = new JsonObject { ["version"] = 99 } };
            File.WriteAllText(host.LayoutPath, saved.ToJsonString());
        });

        host.Open("a1");
        Assert.NotEqual(host.Layout.FindLeaf("a.tree"), host.Layout.FindLeaf("a.view"));
        Assert.Equal(["a.tree", "a.view", "output", "shared", "tools"], DocumentWorkbench.Ids(host.Layout));
        host.Open("b1", DocumentWorkbench.B);
        Assert.Contains("shared", host.Layout.FindLeaf("b.view")!.PanelIds);
    }

    /// <summary>A file from a newer format, or not an object, is discarded and the layouts are seeded.</summary>
    [Theory]
    [InlineData("{\"version\":3,\"layouts\":{}}")]
    [InlineData("{\"version\":\"two\",\"layouts\":{}}")]
    [InlineData("[1,2]")]
    [InlineData("{\"layout\":{\"version\":99}}")]
    [InlineData("{\"nothing\":true}")]
    public void UnusableFilesAreSeededFresh(string content)
    {
        using var host = new DocumentWorkbench();
        host.Restart(() => File.WriteAllText(host.LayoutPath, content));

        host.Open("a1");

        Assert.Equal(["a.tree", "a.view", "output", "shared", "tools"], DocumentWorkbench.Ids(host.Layout));
    }

    /// <summary>The layout of a kind no plugin registers this run is carried along and still there when it returns.</summary>
    [Fact]
    public void LayoutsOfUnknownKindsAreKept()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Layout.MarkChanged();
        host.Restart(AddUnknownKind(host));
        host.Open("a1");
        host.Layout.MarkChanged();
        host.Frame.Draw();

        var after = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(host.LayoutPath))!)["layouts"]!;
        Assert.True(after.ContainsKey("k.gone"));
    }

    /// <summary>Resetting every layout also drops what was saved for kinds that were never opened.</summary>
    [Fact]
    public void ResetAllForgetsSavedLayoutsOfUnopenedKinds()
    {
        using var host = new DocumentWorkbench();
        host.Open("a1");
        host.Layout.MarkChanged();
        host.Restart(AddUnknownKind(host));
        host.Workbench.ResetAllLayouts();
        host.Frame.Draw();
        host.Workbench.SaveLayoutIfChanged();

        var after = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(host.LayoutPath))!)["layouts"]!;
        Assert.False(after.ContainsKey("k.gone"));
    }

    /// <summary>Parked panels, docked or floating, and the known set come back from the file as they were saved.</summary>
    [Fact]
    public void ParkedPanelsRoundTripThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gaya-store-{Guid.NewGuid():N}.json");
        try
        {
            var store = new WorkbenchLayoutStore(NullLogger.Instance, path);
            var layout = new DockLayout();
            layout.DockAtEdge("main", DockZone.Center);
            var parked = new Dictionary<string, ParkedPanel>
            {
                ["docked"] = new(DockZone.Left, "main"),
                ["floating"] = new(DockZone.Center, null, new Rect(1, 2, 3, 4)),
            };

            store.Save(new Dictionary<string, WorkbenchLayoutState> { ["k"] = new(layout, ["main", "closed"], parked) });
            var loaded = store.Load()!.Layouts["k"];

            Assert.Equal(parked["docked"], loaded.ParkedPanels["docked"]);
            Assert.Equal(new Rect(1, 2, 3, 4), loaded.ParkedPanels["floating"].FloatBounds);
            Assert.Equal(["closed", "main"], loaded.KnownPanels.Order());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A layout that cannot be written is logged and swallowed rather than taking the editor down.</summary>
    [Fact]
    public void UnwritableLocationsAreSwallowed()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"gaya-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        try
        {
            var store = new WorkbenchLayoutStore(NullLogger.Instance, Path.Combine(blocker, "layout.json"));

            store.Save(new Dictionary<string, WorkbenchLayoutState>());

            Assert.Null(store.Load());
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    /// <summary>A missing layout file loads as nothing, and saving nothing is refused.</summary>
    [Fact]
    public void StoreLoadReportsMissingFilesAsNothing()
    {
        var store = new WorkbenchLayoutStore(NullLogger.Instance, Path.Combine(Path.GetTempPath(), $"gaya-none-{Guid.NewGuid():N}.json"));

        Assert.Null(store.Load());
        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
    }

    /// <summary>The service opens, activates, deactivates and closes documents and says what changed.</summary>
    [Fact]
    public void DocumentServiceTracksOpenAndActiveDocuments()
    {
        var service = new DocumentService();
        var changes = new List<string?>();
        var closed = new List<string>();
        service.ActiveChanged += document => changes.Add(document?.Id);
        service.Closed += document => closed.Add(document.Id);

        service.Open(DocumentWorkbench.Doc("a"));
        service.Open(DocumentWorkbench.Doc("b"));
        service.Open(DocumentWorkbench.Doc("a"));
        Assert.Equal(["a", "b"], service.Documents.Select(static document => document.Id));
        Assert.Equal("a", service.Active!.Id);
        Assert.Equal(["a", "b", "a"], changes);

        Assert.False(service.Activate("missing"));
        Assert.True(service.Activate("a"));
        service.Deactivate();
        Assert.Null(service.Active);

        Assert.False(service.Close("missing"));
        service.Activate("b");
        Assert.True(service.Close("a"));
        Assert.Equal("b", service.Active!.Id);
        Assert.True(service.Close("b"));
        Assert.Null(service.Active);
        Assert.Empty(service.Documents);
        Assert.Equal(["a", "b"], closed);
        Assert.Equal(["a", "b", "a", null, "b", null], changes);
    }

    /// <summary>Closing the active document falls back to the most recently active one, not the neighbour.</summary>
    [Fact]
    public void ClosingTheActiveDocumentActivatesTheMostRecentOne()
    {
        var service = new DocumentService();
        foreach (var id in new[] { "a", "b", "c" })
        {
            service.Open(DocumentWorkbench.Doc(id));
        }

        service.Activate("a");
        service.Activate("c");
        service.Close("c");

        Assert.Equal("a", service.Active!.Id);
    }

    /// <summary>Invalid documents are refused, as is the id reserved for the no-document layout.</summary>
    [Fact]
    public void DocumentServiceRejectsInvalidDocuments()
    {
        var service = new DocumentService();

        Assert.Throws<ArgumentNullException>(() => service.Open(null!));
        Assert.Throws<ArgumentException>(() => service.Open(new DocumentInfo("", "k", "t")));
        Assert.Throws<ArgumentException>(() => service.Open(new DocumentInfo("a", " ", "t")));
        Assert.Throws<ArgumentException>(() => service.Open(DocumentWorkbench.Doc("a", WorkbenchLayoutStore.NoDocumentKey)));
        Assert.Empty(service.Documents);
    }

    /// <summary>Kinds register and replace by id, and the host application exposes the same service to plugins.</summary>
    [Fact]
    public void KindsRegisterAndTheHostSharesTheDocumentService()
    {
        var registry = new DocumentKindRegistry();
        registry.Register(new DocumentKind("k", "First"));
        registry.Register(new DocumentKind("k", "Second"));

        Assert.Equal("Second", registry.Find("k")!.Title);
        Assert.Single(registry.All);
        Assert.Null(registry.Find("other"));
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        Assert.Throws<ArgumentException>(() => registry.Register(new DocumentKind(" ", "t")));

        using var app = PluginHost.Load([], NullLogger.Instance);
        Assert.Same(app.Documents, app.Services.GetRequiredService<IDocumentService>());
    }
}
