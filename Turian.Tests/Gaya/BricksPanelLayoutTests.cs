namespace Turian.Tests;

/// <summary>Checks the Bricks panel's layout at narrow and wide dock sizes.</summary>
public sealed class BricksPanelLayoutTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-bricks-layout-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Rows keep names separate from versions at narrow and wide dock sizes.</summary>
    [Theory]
    [InlineData(300)]
    [InlineData(900)]
    public async Task RowsDoNotOverlapTheirColumns(int width)
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "game")))!;
        _ = BrickService.New(Path.Combine(project, "Bricks"), "user.mateo.inventory",
            "Inventory with a very long display name");
        var settings = new SettingsService();
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        var controller = TestBricks.Controller(settings,
            new BackgroundTaskRunner(new BackgroundTaskManager(), NullLogger.Instance),
            Substitute.For<IBrickApplier>());
        var panel = new BricksPanel(controller);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(width, 400));
        var font = Font.FromFamilyName("sans-serif", 14);

        for (var frame = 0; frame < 3; frame++)
        {
            surface.Canvas.Clear(SKColors.Black);
            gui.SetStage(Pass.Pass1Build);
            gui.BeginFrame(surface.Canvas, font, font);
            panel.Render(gui);
            gui.CalculateLayout();
            gui.SetStage(Pass.Pass2Render);
            panel.Render(gui);
            gui.Render();
            gui.EndFrame();
        }

        var nodes = Descendants(gui.RootNode!).ToArray();
        var name = Assert.Single(nodes, node => node.Id == "bricks/row/user.mateo.inventory/name");
        var version = Assert.Single(nodes, node => node.Id == "bricks/row/user.mateo.inventory/version");
        Assert.True(name.Rect.X + name.Rect.W <= version.Rect.X);

        if (Environment.GetEnvironmentVariable("TURIAN_TEST_DUMP") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(output, $"bricks-{width}.png"));
            data.SaveTo(file);
        }
    }

    /// <summary>Refreshing preserves registry drafts and rebinds settings when the project changes.</summary>
    [Fact]
    public void InspectorDraftsFollowTheirProject()
    {
        var settings = new SettingsService();
        var controller = TestBricks.Controller(settings,
            new BackgroundTaskRunner(new BackgroundTaskManager(), NullLogger.Instance),
            Substitute.For<IBrickApplier>());
        var selection = new NodeInspectorController(new AssetManager());
        var panel = new BricksPanel(controller, inspector: new TurianBrickInspector(selection, settings));
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        using var surface = SKSurface.Create(new SKImageInfo(640, 480));
        var font = Font.FromFamilyName("sans-serif", 14);
        void Render() => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        Render();
        settings.Set(new AppSettings { ProjectAbsoluteDir = root });
        controller.Refresh();
        controller.Tab = BricksTab.Registries;
        controller.SelectedRegistry = "";
        var registry = controller.InspectSelection()!;
        selection.Select(registry);
        ((ScopedRegistry)registry.Target).Name = "unsaved";
        controller.Refresh();
        Render();
        Assert.Same(registry, selection.SelectedObject);
        Assert.Equal("unsaved", ((ScopedRegistry)registry.Target).Name);
        selection.Select(controller.InspectSettings(settings));
        controller.Refresh();
        Render();
        var original = selection.SelectedObject;
        settings.Set(new AppSettings { ProjectAbsoluteDir = Path.Combine(root, "other") });
        controller.Refresh();
        Render();
        Assert.NotSame(original, selection.SelectedObject);
        Assert.Equal(settings.Settings!.Bricks, Assert.IsType<FormInspection>(selection.SelectedObject).Target);
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
