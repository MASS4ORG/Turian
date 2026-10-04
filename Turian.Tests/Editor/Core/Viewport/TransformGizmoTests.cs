namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="TransformGizmo"/> hit-testing and drag behavior.
/// Uses a real <see cref="EditorCamera"/> (self-contained, no Vulkan dependency) positioned on the
/// -Z axis looking toward +Z, node at the origin, 960×540 viewport, orthographic projection.
/// </summary>
public class TransformGizmoTests
{
    static readonly Vector2 Viewport = new(960f, 540f);

    const float distance = 5f;
    const float gizmoScale = 20f * 90f / 540f;

    readonly EditorCamera camera;
    readonly Node node;
    readonly TransformGizmo gizmo = new();

    /// <summary>Positions an orthographic camera on the -Z axis and a target node at the origin.</summary>
    public TransformGizmoTests()
    {
        camera = new EditorCamera
        {
            Position = new Vector3(0f, 0f, -distance),
            IsOrthographic = true,
            Frustum = 10f,
        };
        node = new Node { Name = "GizmoTarget" };
        gizmo.SelectedNode = node;
    }

    /// <summary>A fresh gizmo defaults to world-space translate with snapping enabled.</summary>
    [Fact]
    public void Defaults_AreTranslateWorldWithSnaps()
    {
        Assert.Equal(TransformGizmoMode.Translate, gizmo.Mode);
        Assert.Equal(TransformGizmoSpace.World, gizmo.Space);
        Assert.Equal(1f, gizmo.SnapTranslation);
        Assert.Equal(15f, gizmo.SnapRotation);
        Assert.Equal(0.1f, gizmo.SnapScale);
        Assert.False(gizmo.IsDragging);
    }

    /// <summary>Pressing the X axis starts a drag that moves the node along world X.</summary>
    [Fact]
    public void PointerDown_OnXAxisStartsDragAndMovesNode()
    {
        var downX = AxisMidpointPixel(TransformGizmoMode.Translate);
        gizmo.Mode = TransformGizmoMode.Translate;
        gizmo.SnapTranslation = 0f;

        gizmo.ProcessPointerDown(downX, camera, Viewport);

        Assert.True(gizmo.IsDragging);
        Assert.Equal(TransformGizmoAxis.X, gizmo.Axis);

        var targetPixel = ProjectToPixel(new Vector3(3f, 0f, 0f));
        gizmo.ProcessPointerMove(targetPixel, camera, Viewport);

        AssertClose(new Vector3(3f - gizmoScale * 0.5f, 0f, 0f), node.Transform.Position);
    }

    /// <summary>Translation with snapping rounds the node position to the unit grid.</summary>
    [Fact]
    public void Translation_WithSnap_RoundsToUnitGrid()
    {
        gizmo.Mode = TransformGizmoMode.Translate;
        gizmo.SnapTranslation = 1f;

        gizmo.ProcessPointerDown(AxisMidpointPixel(TransformGizmoMode.Translate), camera, Viewport);
        gizmo.ProcessPointerMove(ProjectToPixel(new Vector3(3.4f + gizmoScale * 0.5f, 0f, 0f)), camera, Viewport);

        Assert.Equal(3f, node.Transform.Position.X);
        Assert.Equal(0f, node.Transform.Position.Y);
        Assert.Equal(0f, node.Transform.Position.Z);
    }

    /// <summary>Dragging one scale axis scales that axis only.</summary>
    [Fact]
    public void ScaleDrag_AlongAxis_ScalesThatAxisOnly()
    {
        gizmo.Mode = TransformGizmoMode.Scale;
        gizmo.SnapScale = 0f;

        gizmo.ProcessPointerDown(AxisMidpointPixel(TransformGizmoMode.Scale), camera, Viewport);
        Assert.True(gizmo.IsDragging);

        var start = AxisMidpointPixel(TransformGizmoMode.Scale);
        gizmo.ProcessPointerMove(new Vector2(start.X - 90f, start.Y), camera, Viewport);

        Assert.Equal(2f, node.Transform.Scale.X, 3);
        Assert.Equal(1f, node.Transform.Scale.Y, 3);
        Assert.Equal(1f, node.Transform.Scale.Z, 3);
    }

    /// <summary>A pointer press that misses every handle does not start a drag.</summary>
    [Fact]
    public void PointerDown_Miss_DoesNotStartDrag()
    {
        gizmo.ProcessPointerDown(new Vector2(900f, 500f), camera, Viewport);

        Assert.False(gizmo.IsDragging);
        Assert.Equal(TransformGizmoAxis.None, gizmo.Axis);
    }

    /// <summary>Releasing the pointer ends the drag and raises the ended event.</summary>
    [Fact]
    public void PointerUp_EndsDrag()
    {
        var raised = 0;
        gizmo.DragEnded += () => raised++;
        gizmo.ProcessPointerDown(AxisMidpointPixel(TransformGizmoMode.Translate), camera, Viewport);
        Assert.True(gizmo.IsDragging);

        gizmo.ProcessPointerUp();

        Assert.False(gizmo.IsDragging);
        Assert.Equal(1, raised);
    }

    /// <summary>Translation uses solid overlay arrows and three outlined, filled plane handles.</summary>
    [Fact]
    public void Draw_Translate_ProducesLinearHandlesAndPlanes()
    {
        var g = new Gizmos();
        gizmo.Mode = TransformGizmoMode.Translate;

        gizmo.Draw(g, camera, Viewport);

        Assert.Equal(12, g.LineCount);
        Assert.NotEmpty(g.OverlayTriangles);
        Assert.Empty(g.WorldLines);
        Assert.Empty(g.WorldTriangles);
    }

    /// <summary>Scale uses solid boxes and plane handles with a center box for uniform scaling.</summary>
    [Fact]
    public void Draw_Scale_UsesSolidBoxes()
    {
        var g = new Gizmos();
        gizmo.Mode = TransformGizmoMode.Scale;

        gizmo.Draw(g, camera, Viewport);

        Assert.Equal(12, g.LineCount);
        Assert.NotEmpty(g.OverlayTriangles);
    }

    /// <summary>Rotate mode draws one arc per axis.</summary>
    [Fact]
    public void Draw_Rotate_ProducesThreeArcs()
    {
        var g = new Gizmos();
        gizmo.Mode = TransformGizmoMode.Rotate;

        gizmo.Draw(g, camera, Viewport);

        Assert.Equal(3 * 64, g.LineCount);
        Assert.All(g.OverlayLines, line => Assert.Equal(4f, line.Thickness));
    }

    /// <summary>With no node selected the gizmo draws nothing.</summary>
    [Fact]
    public void Draw_WithNoSelection_ProducesNothing()
    {
        gizmo.SelectedNode = null;

        var g = new Gizmos();
        gizmo.Draw(g, camera, Viewport);

        Assert.Equal(0, g.LineCount);
    }

    static Vector2 AxisMidpointPixel(TransformGizmoMode mode)
    {
        _ = mode;
        var endPx = ProjectToPixel(new Vector3(gizmoScale, 0f, 0f));
        return new Vector2((480f + endPx.X) * 0.5f, (270f + endPx.Y) * 0.5f);
    }

    static Vector2 ProjectToPixel(Vector3 world)
    {
        var cam = new EditorCamera
        {
            Position = new Vector3(0f, 0f, -distance),
            IsOrthographic = true,
            Frustum = 10f,
        };
        var p = cam.Project(world);
        return new Vector2((p.X + 1f) * 0.5f * Viewport.X, (p.Y + 1f) * 0.5f * Viewport.Y);
    }

    static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 2);
        Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected.Z, actual.Z, 2);
    }
}
