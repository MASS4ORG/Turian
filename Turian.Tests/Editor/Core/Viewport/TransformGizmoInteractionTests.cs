namespace Turian.Tests;

/// <summary>Checks plane editing, combined handles, fixed screen size and rotation feedback.</summary>
public sealed class TransformGizmoInteractionTests
{
    static readonly Vector2 Viewport = new(960, 540);
    const float Scale = 20f * 90f / 540f;

    static (EditorCamera Camera, Node Node, TransformGizmo Gizmo) Create(TransformGizmoMode mode)
    {
        var camera = new EditorCamera { Position = new Vector3(0, 0, -5), IsOrthographic = true, Frustum = 10f };
        camera.Resize(960, 540);
        var node = new Node();
        return (camera, node, new TransformGizmo
        {
            SelectedNode = node,
            Mode = mode,
            SnapTranslation = 0f,
            SnapScale = 0f,
            SnapRotation = 0f
        });
    }

    /// <summary>Every tool draws in the overlay pass without changing the caller's drawing state.</summary>
    [Theory]
    [InlineData(TransformGizmoMode.Translate)]
    [InlineData(TransformGizmoMode.Rotate)]
    [InlineData(TransformGizmoMode.Scale)]
    [InlineData(TransformGizmoMode.Combined)]
    public void Draw_IsOverlayAndPreservesState(TransformGizmoMode mode)
    {
        var (camera, _, gizmo) = Create(mode);
        var drawing = new Gizmos
        {
            Matrix = Matrix4x4.CreateTranslation(9, 9, 9),
            Thickness = 7f,
            Color = new Vector4(0.2f, 0.3f, 0.4f, 1f)
        };
        var state = (drawing.Matrix, drawing.Thickness, drawing.Color, drawing.DepthTest);
        gizmo.Draw(drawing, camera, Viewport);
        Assert.Empty(drawing.WorldLines);
        Assert.Empty(drawing.WorldTriangles);
        Assert.True(drawing.LineCount + drawing.TriangleCount > 0);
        Assert.Equal(state, (drawing.Matrix, drawing.Thickness, drawing.Color, drawing.DepthTest));
    }

    /// <summary>The plane handle translates both selected axes with no jump on pointer-down.</summary>
    [Fact]
    public void PlaneTranslation_MovesBothAxesWithoutJump()
    {
        var (camera, node, gizmo) = Create(TransformGizmoMode.Translate);
        var start = Pixel(camera, new Vector3(Scale * 0.33f, Scale * 0.33f, 0));
        gizmo.ProcessPointerDown(start, camera, Viewport);
        Assert.Equal(TransformGizmoAxis.Xy, gizmo.Axis);
        gizmo.ProcessPointerMove(start, camera, Viewport);
        Assert.Equal(Vector3.Zero, node.Position);
        gizmo.ProcessPointerMove(Pixel(camera, new Vector3(Scale * 0.33f + 1f, Scale * 0.33f + 2f, 0)),
            camera, Viewport);
        Assert.Equal(1f, node.Position.X, 4);
        Assert.Equal(2f, node.Position.Y, 4);
        Assert.Equal(0f, node.Position.Z);
    }

    /// <summary>Scaling a plane affects its two dimensions and leaves the third dimension alone.</summary>
    [Fact]
    public void PlaneScale_ScalesTwoAxes()
    {
        var (camera, node, gizmo) = Create(TransformGizmoMode.Scale);
        var start = Pixel(camera, new Vector3(Scale * 0.33f, Scale * 0.33f, 0));
        gizmo.ProcessPointerDown(start, camera, Viewport);
        Assert.Equal(TransformGizmoAxis.Xy, gizmo.Axis);
        gizmo.ProcessPointerMove(start + new Vector2(-90, -90), camera, Viewport);
        Assert.Equal(new Vector3(2, 2, 1), node.Scale);
    }

    /// <summary>The center box scales uniformly and raises the transform mutation event.</summary>
    [Theory]
    [InlineData(TransformGizmoMode.Scale)]
    [InlineData(TransformGizmoMode.Combined)]
    public void CenterScale_IsUniformAndNotifies(TransformGizmoMode mode)
    {
        var (camera, node, gizmo) = Create(mode);
        var edits = 0;
        gizmo.TransformEdited += () => edits++;
        gizmo.ProcessPointerDown(Pixel(camera, Vector3.Zero), camera, Viewport);
        Assert.Equal(TransformGizmoAxis.Center, gizmo.Axis);
        gizmo.ProcessPointerMove(Pixel(camera, Vector3.Zero) + new Vector2(100, -100), camera, Viewport);
        Assert.Equal(new Vector3(2), node.Scale);
        Assert.Equal(1, edits);
    }

    /// <summary>Combined scale stubs extend beyond translation arrows without sharing their hit regions.</summary>
    [Fact]
    public void Combined_SeparatesTranslationAndScale()
    {
        var (camera, node, gizmo) = Create(TransformGizmoMode.Combined);
        var start = Pixel(camera, new Vector3(Scale * 0.6f, 0, 0));
        gizmo.ProcessPointerDown(start, camera, Viewport);
        Assert.Equal(TransformGizmoMode.Translate, gizmo.HandleMode);
        gizmo.ProcessPointerMove(start + new Vector2(-27, 0), camera, Viewport);
        Assert.Equal(1f, node.Position.X, 4);
        gizmo.ProcessPointerUp();
        start = Pixel(camera, node.Position + new Vector3(Scale * 1.5f, 0, 0));
        gizmo.ProcessPointerDown(start, camera, Viewport);
        Assert.Equal(TransformGizmoMode.Scale, gizmo.HandleMode);
        gizmo.ProcessPointerMove(start + new Vector2(-90, 0), camera, Viewport);
        Assert.Equal(new Vector3(2, 1, 1), node.Scale);
    }

    /// <summary>Hidden ring halves cannot acquire a gesture and hover clears when leaving the viewport.</summary>
    [Fact]
    public void Rotation_HitTestingMatchesVisibleHalfAndClearsHover()
    {
        var (camera, _, gizmo) = Create(TransformGizmoMode.Rotate);
        var radius = Scale * 0.85f;
        var front = Pixel(camera, new Vector3(-radius * 0.8f, -radius * 0.6f, 0));
        gizmo.ProcessPointerMove(front, camera, Viewport);
        Assert.Equal(TransformGizmoAxis.Z, gizmo.Axis);
        var drawing = new Gizmos();
        gizmo.Draw(drawing, camera, Viewport);
        Assert.Contains(drawing.OverlayLines, line => line.A == Vector3.Zero);
        Assert.Contains(drawing.OverlayLines, line => line.Thickness == 6f);
        gizmo.ClearHover();
        Assert.Equal(TransformGizmoAxis.None, gizmo.Axis);
        gizmo.ProcessPointerDown(Pixel(camera, new Vector3(radius * 0.8f, -radius * 0.6f, 0)), camera, Viewport);
        Assert.True(gizmo.IsDragging);
        gizmo.ProcessPointerUp();
        camera.Position = new Vector3(-3, -2, -5);
        camera.SetYawPitch(MathF.Atan2(3, 5), -MathF.Atan2(2, MathF.Sqrt(34)));
        gizmo.ProcessPointerDown(Pixel(camera, new Vector3(radius * 0.8f, radius * 0.6f, 0)), camera, Viewport);
        Assert.False(gizmo.IsDragging);
    }

    /// <summary>Handles keep the same projected length across camera distances and projection types.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Handles_KeepConstantPixelSize(bool orthographic)
    {
        var (camera, _, gizmo) = Create(TransformGizmoMode.Translate);
        camera.IsOrthographic = orthographic;
        var first = Extent();
        camera.Position *= 4f;
        camera.Frustum *= 4f;
        Assert.Equal(first, Extent(), 2);

        float Extent()
        {
            var drawing = new Gizmos();
            gizmo.Draw(drawing, camera, Viewport);
            return drawing.OverlayTriangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })
                .Max(point => MathF.Abs(Pixel(camera, point).X - 480));
        }
    }

    /// <summary>The upper Y handle increases world height or Y scale when dragged upward.</summary>
    [Theory]
    [InlineData(TransformGizmoMode.Translate, false)]
    [InlineData(TransformGizmoMode.Translate, true)]
    [InlineData(TransformGizmoMode.Scale, false)]
    [InlineData(TransformGizmoMode.Scale, true)]
    public void YHandleAbovePivot_DraggingUpIncreasesY(TransformGizmoMode mode, bool orthographic)
    {
        var (camera, node, gizmo) = Create(mode);
        camera.IsOrthographic = orthographic;
        var start = Viewport * 0.5f - new Vector2(0, 60);
        gizmo.ProcessPointerDown(start, camera, Viewport);
        Assert.True(gizmo.IsDragging);
        Assert.Equal(TransformGizmoAxis.Y, gizmo.Axis);
        gizmo.ProcessPointerMove(start - new Vector2(0, 30), camera, Viewport);
        Assert.True(mode == TransformGizmoMode.Translate ? node.Position.Y > 0f : node.Scale.Y > 1f);
        Assert.Equal(0f, node.Position.X);
        Assert.Equal(0f, node.Position.Z);
        Assert.Equal(1f, node.Scale.X);
        Assert.Equal(1f, node.Scale.Z);
    }

    static Vector2 Pixel(EditorCamera camera, Vector3 point) =>
        (camera.Project(point) + Vector2.One) * 0.5f * Viewport;
}
