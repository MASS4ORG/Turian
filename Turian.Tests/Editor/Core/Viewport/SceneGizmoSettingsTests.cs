namespace Turian.Tests;

/// <summary>Checks shared axis colors, select mode and independent snap enablement.</summary>
public sealed class SceneGizmoSettingsTests
{
    /// <summary>Transform handles and origin lines use the same editable, clamped RGB palette.</summary>
    [Fact]
    public void AxisColorsReachHandlesAndGrid()
    {
        var settings = new SceneGizmoSettings { XColor = new Vector3(0.2f, 0.4f, 0.6f) };
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), settings.AxisColor(-Vector3.UnitX));
        var drawing = new Gizmos();
        var gizmo = new TransformGizmo { Settings = settings, SelectedNode = new Node() };
        var camera = new EditorCamera();
        gizmo.Draw(drawing, camera, new Vector2(960, 540));
        Assert.Contains(drawing.OverlayLines, line => line.Color == settings.AxisColor(Vector3.UnitX, 0.85f));
        drawing = new Gizmos();
        GroundGrid.Draw(drawing, Vector3.Zero, new SceneGridSettings { HalfExtent = 5 }, settings);
        Assert.Contains(drawing.WorldLines, line => line.Color == settings.AxisColor(Vector3.UnitX, 0.65f));
        settings.XColor = new Vector3(-1, 2, 0.5f);
        Assert.Equal(new Vector4(0, 1, 0.5f, 1), settings.AxisColor(Vector3.UnitX, 2));
    }

    /// <summary>Select mode and hidden gizmos neither draw handles nor consume transformation gestures.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectAndHiddenGizmosDoNotEditObjects(bool select)
    {
        var camera = new EditorCamera { Position = new Vector3(0, 0, -5), IsOrthographic = true };
        var node = new Node();
        var gizmo = new TransformGizmo { SelectedNode = node };
        var size = new Vector2(960, 540);
        var pointer = size * 0.5f - new Vector2(0, 60);
        gizmo.ProcessPointerMove(pointer, camera, size);
        Assert.Equal(TransformGizmoAxis.Y, gizmo.Axis);
        if (select) gizmo.Mode = TransformGizmoMode.Select;
        else gizmo.Settings.Visible = false;
        gizmo.ProcessPointerMove(pointer, camera, size);
        Assert.Equal(TransformGizmoAxis.None, gizmo.Axis);
        gizmo.ProcessPointerDown(pointer, camera, size);
        Assert.False(gizmo.IsDragging);
        var drawing = new Gizmos();
        gizmo.Draw(drawing, camera, size);
        Assert.Equal(0, drawing.TriangleCount + drawing.LineCount);
        gizmo.SelectedNode = null;
        gizmo.ProcessPointerMove(pointer, camera, size);
        Assert.Equal(Vector3.Zero, node.Position);
    }

    /// <summary>Disabling snapping keeps all intervals and permits unsnapped translation and scaling.</summary>
    [Theory]
    [InlineData(TransformGizmoMode.Translate)]
    [InlineData(TransformGizmoMode.Scale)]
    public void SnapTogglePreservesIntervals(TransformGizmoMode mode)
    {
        var camera = new EditorCamera { Position = new Vector3(0, 0, -5), IsOrthographic = true };
        var gizmo = new TransformGizmo { SelectedNode = new Node(), Mode = mode, SnapEnabled = false };
        var size = new Vector2(960, 540);
        var pointer = size * 0.5f - new Vector2(0, 60);
        gizmo.ProcessPointerDown(pointer, camera, size);
        gizmo.ProcessPointerMove(pointer - new Vector2(0, 9), camera, size);
        Assert.Equal(1, gizmo.SnapTranslation);
        Assert.Equal(15, gizmo.SnapRotation);
        Assert.Equal(0.1f, gizmo.SnapScale);
        if (mode == TransformGizmoMode.Translate) Assert.Equal(1f / 3f, gizmo.SelectedNode.Position.Y, 4);
        else Assert.Equal(1.1f, gizmo.SelectedNode.Scale.Y, 4);
    }
}
