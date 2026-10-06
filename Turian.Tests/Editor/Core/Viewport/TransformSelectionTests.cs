namespace Turian.Tests;

/// <summary>Checks group transform pivots and avoids applying gestures twice to selected descendants.</summary>
public sealed class TransformSelectionTests
{
    /// <summary>World-space translation retains offsets and uses gesture snapshots on repeated updates.</summary>
    [Fact]
    public void TranslationMovesSelectedRootsOnce()
    {
        var parent = new Node { Position = new Vector3(1, 2, 3) };
        var child = new Node { Parent = parent, Position = new Vector3(4, 0, 0) };
        parent.Children.Add(child);
        var other = new Node { Position = new Vector3(-2, 0, 0) };
        var gesture = new TransformSelection([parent, child, other], Vector3.Zero, Quaternion.Identity);
        gesture.Translate(new Vector3(3, 0, 0));
        gesture.Translate(new Vector3(3, 0, 0));
        Assert.Equal(new Vector3(4, 2, 3), parent.Position);
        Assert.Equal(new Vector3(8, 2, 3), child.GlobalTransform.Position);
        Assert.Equal(new Vector3(1, 0, 0), other.Position);
        Assert.Equal([parent, other], SelectionService.TopLevelNodes([parent, child, other, parent]));
    }

    /// <summary>Group rotation rotates both orientations and object positions around the shared pivot.</summary>
    [Fact]
    public void RotationUsesSharedPivot()
    {
        var first = new Node { Position = Vector3.UnitX };
        var second = new Node { Position = -Vector3.UnitX };
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var gesture = new TransformSelection([first, second], Vector3.Zero, Quaternion.Identity);
        gesture.Rotate(rotation);
        Assert.True(Vector3.Distance(Vector3.UnitY, first.Position) < 0.0001f);
        Assert.True(Vector3.Distance(-Vector3.UnitY, second.Position) < 0.0001f);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, first.Orientation)) > 0.9999f);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, second.Orientation)) > 0.9999f);
    }

    /// <summary>Group scaling preserves each original scale and scales offsets in local gizmo axes.</summary>
    [Fact]
    public void ScaleUsesEachOriginalScaleAndLocalAxes()
    {
        var first = new Node { Position = Vector3.UnitY, Scale = new Vector3(2) };
        var second = new Node { Position = -Vector3.UnitY, Scale = new Vector3(3) };
        var axes = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var gesture = new TransformSelection([first, second], Vector3.Zero, axes);
        gesture.Scale(new Vector3(2, 1, 1));
        Assert.True(Vector3.Distance(new Vector3(0, 2, 0), first.Position) < 0.0001f);
        Assert.True(Vector3.Distance(new Vector3(0, -2, 0), second.Position) < 0.0001f);
        Assert.Equal(new Vector3(4, 2, 2), first.Scale);
        Assert.Equal(new Vector3(6, 3, 3), second.Scale);
        Assert.Equal(Vector3.Zero, TransformSelection.GetPivot([first, second], first, center: true));
        Assert.Equal(first.Position, TransformSelection.GetPivot([first, second], first, center: false));
        Assert.Equal(Vector3.Zero, TransformSelection.GetPivot([], null, center: true));
    }

    /// <summary>The centre handle moves all selected objects and retains the centre-to-object distances.</summary>
    [Fact]
    public void GizmoTranslationReachesAllSelectedObjects()
    {
        var first = new Node { Position = new Vector3(-1, 0, 0) };
        var second = new Node { Position = new Vector3(1, 0, 0) };
        var camera = new EditorCamera { Position = new Vector3(0, 0, -5), IsOrthographic = true, Frustum = 10 };
        camera.Resize(960, 540);
        var size = new Vector2(960, 540);
        var gizmo = new TransformGizmo { SelectedNode = second, SelectedNodes = [first, second], CenterPivot = true };
        Assert.Equal(Vector3.Zero, gizmo.PivotPosition);
        gizmo.ProcessPointerDown(size / 2, camera, size);
        Assert.True(gizmo.IsDragging);
        gizmo.ProcessPointerMove(size / 2 + new Vector2(-27, 0), camera, size);
        gizmo.ProcessPointerUp();
        Assert.Equal(0f, first.Position.X, 4);
        Assert.Equal(2f, second.Position.X, 4);
        Assert.True(Vector3.Distance(new Vector3(1, 0, 0), gizmo.PivotPosition) < 0.0001f);
    }
}
