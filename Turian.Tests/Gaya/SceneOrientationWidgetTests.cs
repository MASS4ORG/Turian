namespace Turian.Tests;

/// <summary>Exercises orientation hover and clicks through Guinevere's two GUI passes.</summary>
public sealed class SceneOrientationWidgetTests
{
    /// <summary>Signed axes and the cube change the camera only during input processing and retain selection framing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WidgetClickSnapsAxisAndCenterTogglesProjection(bool negative)
    {
        var camera = new EditorCamera { Position = new Vector3(3, 2, -5) };
        camera.LookIn(-camera.Position, Vector3.UnitY);
        var controller = new SceneCameraController(camera);
        var target = new Node();
        var widget = new SceneOrientationWidget();
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        var node = Descendants(gui.RootNode!).Single(child => child.Id == "scene/orientation");
        Assert.InRange(node.Rect.X, 400f, 425f);
        Assert.InRange(node.Rect.Y, 10f, 16f);
        Assert.InRange(node.Rect.W, 64f, 85f);
        Assert.DoesNotContain(Descendants(gui.RootNode!), child => child.Id == "scene/orientation/projection");
        var center = node.Rect.Center;
        var axis = negative ? -Vector3.UnitX : Vector3.UnitX;
        var marker = SceneOrientationGizmo.Markers(camera).Single(item => item.Direction == axis);
        input.MousePosition.Returns(center + marker.Offset);
        Frame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        var before = camera.Orientation;
        InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
        {
            widget.Render(current, controller, target);
            if (current.Pass == Pass.Pass1Build) Assert.Equal(before, camera.Orientation);
        });
        Assert.True(camera.IsOrthographic);
        Assert.True(Vector3.Dot(camera.Front, -axis) > 0.99999f);
        Assert.True(camera.Project(target.Position).Length() < 1e-5f);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.MousePosition.Returns(center);
        Frame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        Frame();
        Assert.False(camera.IsOrthographic);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.MousePosition.Returns(center + new Vector2(45, 40));
        Frame();
        input.MousePosition.Returns(center);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        InspectorFormsRenderingTests.Frame(gui, surface, font, current => widget.Render(current, null, null));

        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font,
            current => widget.Render(current, controller, target));
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));

    /// <summary>Negative endpoints retain a solid pastel fill regardless of the content beneath the widget.</summary>
    [Fact]
    public void NegativeAxisColorIsOpaqueAndLighter()
    {
        var camera = new EditorCamera();
        camera.LookIn(new Vector3(3, 2, 4), Vector3.UnitY);
        var controller = new SceneCameraController(camera);
        var widget = new SceneOrientationWidget();
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        var node = Descendants(gui.RootNode!).Single(child => child.Id == "scene/orientation");
        var negative = SceneOrientationGizmo.Markers(camera).Single(marker => marker.Direction == -Vector3.UnitY);
        var point = node.Rect.Center + negative.Offset;
        surface.Canvas.Clear(SKColors.Black);
        Frame();
        var black = Pixel();
        surface.Canvas.Clear(SKColors.DarkBlue);
        Frame();
        Assert.Equal(black, Pixel());
        Assert.True(black.Red > 130 && black.Green > 220 && black.Blue > 150);

        SKColor Pixel()
        {
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            return bitmap.GetPixel((int)point.X, (int)point.Y);
        }

        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font,
            current => widget.Render(current, controller, null));
    }
}
