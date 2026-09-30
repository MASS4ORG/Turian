namespace Turian.Tests;

/// <summary>Checks the Bricks panel's layout at narrow and wide dock sizes.</summary>
public sealed class BricksPanelLayoutTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-bricks-layout-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Rows keep names separate from versions, and narrow panels can scroll to every control.</summary>
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
        var controller = new BricksController(settings,
            new BackgroundTaskRunner(new BackgroundTaskManager(), NullLogger.Instance),
            Substitute.For<IBrickApplier>(), NullLogger.Instance);
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
        Assert.Contains(nodes, node => gui.GetScrollState(node.Id) is { IsScrollingX: true, IsScrollingY: true });
        if (width == 300)
            Assert.Contains(nodes, node => gui.GetScrollState(node.Id) is { ShowScrollbarX: true });

        if (Environment.GetEnvironmentVariable("TURIAN_TEST_DUMP") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(output, $"bricks-{width}.png"));
            data.SaveTo(file);
        }
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
