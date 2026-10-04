namespace Turian.Tests;

/// <summary>Checks that each combined-tool snap field edits the corresponding operation.</summary>
public sealed class ScenePanelSnapTests
{
    /// <summary>Typing in a snap field updates only its operation, including the three combined fields.</summary>
    [Theory]
    [InlineData(TransformGizmoMode.Translate, null)]
    [InlineData(TransformGizmoMode.Rotate, null)]
    [InlineData(TransformGizmoMode.Scale, null)]
    [InlineData(TransformGizmoMode.Translate, "M")]
    [InlineData(TransformGizmoMode.Rotate, "R")]
    [InlineData(TransformGizmoMode.Scale, "S")]
    public void SnapFieldEditsOnlyItsOperation(TransformGizmoMode operation, string? label)
    {
        var gizmo = new TransformGizmo { SnapTranslation = 1, SnapRotation = 15, SnapScale = 0.1f };
        var snapField = typeof(ScenePanel).GetMethod("SnapField", BindingFlags.Static | BindingFlags.NonPublic)!;
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        input.GetTypedCharacters().Returns(string.Empty);
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(300, 100));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        var field = Descendants(gui.RootNode!).Single(node => MathF.Abs(node.Rect.W - 64) < 0.1f);
        input.MousePosition.Returns(new Vector2(field.Rect.X + 5, field.Rect.Y + 5));
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        Frame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsKeyDown(GKey.LeftControl).Returns(true);
        input.IsKeyPressed(GKey.A).Returns(true);
        Frame();
        input.IsKeyDown(GKey.LeftControl).Returns(false);
        input.IsKeyPressed(GKey.A).Returns(false);
        input.GetTypedCharacters().Returns("2.5");
        Frame();
        Assert.Equal(operation == TransformGizmoMode.Translate ? 2.5f : 1f, gizmo.SnapTranslation);
        Assert.Equal(operation == TransformGizmoMode.Rotate ? 2.5f : 15f, gizmo.SnapRotation);
        Assert.Equal(operation == TransformGizmoMode.Scale ? 2.5f : 0.1f, gizmo.SnapScale);

        input.GetTypedCharacters().Returns(string.Empty);
        input.IsKeyDown(GKey.LeftControl).Returns(true);
        input.IsKeyPressed(GKey.A).Returns(true);
        Frame();
        input.IsKeyDown(GKey.LeftControl).Returns(false);
        input.IsKeyPressed(GKey.A).Returns(false);
        input.GetTypedCharacters().Returns("invalid");
        Frame();
        Assert.Equal(operation == TransformGizmoMode.Rotate ? 2.5f : 15f, gizmo.SnapRotation);

        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
        {
            using (current.Node(300, 100, "snapHost").Direction(Axis.Horizontal).Enter())
                snapField.Invoke(null, [current, gizmo, operation, label]);
        });
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
