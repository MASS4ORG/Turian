namespace Turian.Tests;

/// <summary>
/// The Bricks panel as a user drives it: registry-only bricks found by search, bake twins shown as their source, and
/// checkbox changes staged until Apply brings the project in line once.
/// </summary>
public sealed class BricksPanelChangesTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-bricks-changes-").FullName;
    readonly MemoryBrickWorkspace workspace;
    readonly BricksController controller;
    readonly BricksPanel panel;
    readonly IInputHandler input = Substitute.For<IInputHandler>();
    readonly Gui gui;
    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(900, 600));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);

    /// <summary>Opens the panel on an in-memory project whose registry lists two bricks.</summary>
    public BricksPanelChangesTests()
    {
        workspace = new MemoryBrickWorkspace(root);
        controller = new BricksController(null, workspace) { Store = new PackageStore(Path.Combine(root, "store")) };
        panel = new BricksPanel(controller);
        input.MousePosition.Returns(new Vector2(-1));
        gui = new Gui { Input = input };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        surface.Dispose();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Typing part of a name only a registry knows lists that brick, marked with the scope it installs into.</summary>
    [Fact]
    public async Task SearchFindsBricksOnlyARegistryLists()
    {
        var studioOnly = new PackageManifest
        {
            Name = "user.mateo.inspector",
            Version = SemanticVersion.Parse("1.0.2"),
            DisplayName = "Wire Inspector",
            Scopes = [PackageScope.Studio],
        };
        workspace.Listings = new RegistryCatalog(
        [
            Listing("user.mateo.inspector") with { ManifestJson = studioOnly.ToJson() },
            Listing("user.mateo.other"),
        ], []);
        await Open();

        panel.Search = "wire";
        Frame();

        var ids = Nodes().Select(static n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("bricks/row/user.mateo.inspector", ids);
        Assert.Contains("bricks/row/user.mateo.inspector/scope", ids);
        Assert.DoesNotContain("bricks/row/user.mateo.other", ids);
    }

    /// <summary>A bake twin published under its own id is listed once, as its source, and the inspector names both forms.</summary>
    [Fact]
    public async Task TwinsAreOneBrickWithTheirForms()
    {
        Directory.CreateDirectory(controller.Store.Root);
        Directory.Move(BrickService.New(root, "user.mateo.save"), Path.Combine(controller.Store.Root, "user.mateo.save@a"));
        var twin = BrickService.New(root, "user.mateo.save.bake");
        new PackageManifest
        {
            Name = "user.mateo.save.bake",
            Version = SemanticVersion.Parse("0.1.0"),
            BakeOf = new PackageBakeOf { Brick = "user.mateo.save", Version = SemanticVersion.Parse("0.1.0"), Integrity = "sha256-x" },
        }.Save(twin);
        Directory.Move(twin, Path.Combine(controller.Store.Root, "user.mateo.save.bake@b"));
        await Open();

        var ids = Nodes().Select(static n => n.Id).ToList();
        Assert.Contains("bricks/row/user.mateo.save", ids);
        Assert.DoesNotContain(ids, static id => id.StartsWith("bricks/row/user.mateo.save.bake", StringComparison.Ordinal));
        var brick = controller.Catalog.Single(static b => b.Id == "user.mateo.save");
        var versions = BrickInspections.Brick(controller, brick, null).Model.Sections.Single(static s => s.Title == "Versions");
        Assert.Equal("Source, Baked twin", versions.BodyFields.Single(static f => f.Label == "Forms Published").GetValue());
    }

    /// <summary>Ticking several bricks stages them; Apply enables them all and brings the project in line once.</summary>
    [Fact]
    public async Task CheckboxesStageChangesThatApplyTogether()
    {
        workspace.Listings = new RegistryCatalog([Listing("user.mateo.first"), Listing("user.mateo.second")], []);
        await Open();
        var before = workspace.Applied;

        Click("bricks/row/user.mateo.first/enabled");
        Click("bricks/row/user.mateo.second/enabled");

        Assert.Equal(["user.mateo.first", "user.mateo.second"], controller.Pending.Select(static c => c.Id));
        Assert.Empty(workspace.Used);
        Assert.Equal(before, workspace.Applied);

        Click("bricks/pending/apply");
        await Until(() => !controller.IsBusy && workspace.Used.Count == 2);

        Assert.Equal(["user.mateo.first", "user.mateo.second"], workspace.Used);
        Assert.Equal(before + 1, workspace.Applied);
        Assert.Empty(controller.Pending);
        Frame();
        Assert.DoesNotContain(Nodes(), static n => n.Id == "bricks/pending");
    }

    /// <summary>Ticking a staged brick again drops its change instead of staging the opposite one.</summary>
    [Fact]
    public async Task TickingAgainUndoesTheStagedChange()
    {
        workspace.Listings = new RegistryCatalog([Listing("user.mateo.first")], []);
        await Open();

        Click("bricks/row/user.mateo.first/enabled");
        Assert.Single(controller.Pending);
        Click("bricks/row/user.mateo.first/enabled");

        Assert.Empty(controller.Pending);
    }

    /// <summary>
    /// State an action changes between a frame's layout and its drawing appears on the next frame, never as a node the
    /// layout did not place, which would flash at the window's corner.
    /// </summary>
    [Fact]
    public async Task ChangesBetweenPassesWaitForTheNextFrame()
    {
        workspace.Listings = new RegistryCatalog([Listing("user.mateo.first")], []);
        await Open();

        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        panel.Render(gui);
        gui.CalculateLayout();
        controller.Stage(new BrickChange(BrickChangeKind.Enable, "user.mateo.first", BrickScope.Project, "^1.0.0"));
        gui.SetStage(Pass.Pass2Render);
        panel.Render(gui);
        gui.Render();
        gui.EndFrame();

        Assert.DoesNotContain(Nodes(), static n => n.Id == "bricks/pending");
        Frame();
        Assert.Contains(Nodes(), static n => n.Id == "bricks/pending" && n.Rect.Y > 0);
    }

    static RegistryListing Listing(string id) => new("bricks.example", id, [new ListedVersion("1.0.0", Yanked: false)]);

    // Draws until the first registry refresh, which the first frame starts, has restored and published.
    async Task Open()
    {
        Frame();
        await workspace.Restored.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Until(() => !controller.IsBusy);
        Frame();
    }

    void Frame()
    {
        gui.Time.Update(0.016);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
    }

    void Click(string id)
    {
        var node = Assert.Single(Nodes(), n => n.Id == id);
        input.MousePosition.Returns(node.Center);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame();
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        Frame();
        input.MousePosition.Returns(new Vector2(-1));
        Frame();
    }

    IEnumerable<LayoutNode> Nodes() => Descendants(gui.RootNode!);

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) => new[] { node }.Concat(node.Children.SelectMany(Descendants));

    static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
