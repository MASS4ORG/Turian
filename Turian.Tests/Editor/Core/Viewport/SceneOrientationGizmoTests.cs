namespace Turian.Tests;

/// <summary>Checks signed axis picking and camera projection changes around a stable pivot.</summary>
public sealed class SceneOrientationGizmoTests
{
    /// <summary>All six world directions snap to the correct view without losing the selected pivot.</summary>
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    public void AxisClickFramesPivotFromSignedDirection(float x, float y, float z)
    {
        var camera = new EditorCamera { Position = new Vector3(3, 2, -5) };
        var pivot = new Vector3(1, -2, 3);
        var distance = Vector3.Distance(camera.Position, pivot);
        var controller = new SceneCameraController(camera);
        var axis = new Vector3(x, y, z);
        controller.AlignToAxis(axis, pivot);
        Assert.True(camera.IsOrthographic);
        Assert.True(Vector3.Dot(camera.Front, -axis) > 0.99999f);
        Assert.Equal(distance, Vector3.Distance(camera.Position, pivot), 4);
        Assert.True(camera.Project(pivot).Length() < 1e-5f);
        Assert.True(float.IsFinite(camera.Up.X + camera.Up.Y + camera.Up.Z));
        controller.AlignToAxis(axis);
        Assert.True(camera.IsOrthographic);
        controller.AlignToAxis(Vector3.Zero);
        Assert.True(Vector3.Dot(camera.Front, -axis) > 0.99999f);
    }

    /// <summary>Projection toggles preserve framing and screen orientation in both directions.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(2f, 1f)]
    public void ProjectionTogglePreservesFramingAndOrientation(float offsetX, float offsetY)
    {
        var camera = new EditorCamera();
        camera.Resize(960, 540);
        var controller = new SceneCameraController(camera);
        var pivot = camera.Position + camera.Front * 5 + camera.Right * offsetX + camera.Up * offsetY;
        var point = pivot + camera.Up + camera.Right;
        var pixel = camera.Project(point);
        var orientation = camera.Orientation;
        var position = camera.Position;
        controller.ToggleProjection(pivot);
        Assert.True(camera.IsOrthographic);
        Assert.True(Vector2.Distance(pixel, camera.Project(point)) < 1e-5f);
        Assert.Equal(orientation, camera.Orientation);
        controller.ToggleProjection();
        Assert.False(camera.IsOrthographic);
        Assert.True(Vector3.Distance(position, camera.Position) < 1e-5f);
        Assert.True(Vector2.Distance(pixel, camera.Project(point)) < 1e-5f);
    }

    /// <summary>Orthographic wheel zoom changes the viewing volume instead of moving an ineffective camera.</summary>
    [Fact]
    public void OrthographicWheelZoomChangesFrustum()
    {
        var camera = new EditorCamera { IsOrthographic = true, Frustum = 10 };
        var position = camera.Position;
        var controller = new SceneCameraController(camera);
        controller.OnWheel(1);
        Assert.Equal(9, camera.Frustum);
        controller.OnWheel(-1);
        Assert.Equal(9.9f, camera.Frustum, 4);
        Assert.Equal(position, camera.Position);
        controller.OnWheel(1000);
        Assert.Equal(0.01f, camera.Frustum);
        camera.IsOrthographic = false;
        controller.IsLeftButton = controller.IsAlt = true;
        controller.OnMouseDown(0, 0);
        controller.OnWheel(1);
        Assert.True(Vector3.Distance(position, camera.Position) > 0f);
    }

    /// <summary>Signed axis endpoints are picked from the visible side and the cube toggles projection.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkersAndCubeFollowCameraRotation(bool orthographic)
    {
        var camera = new EditorCamera { IsOrthographic = orthographic };
        camera.LookIn(new Vector3(3, 2, 4), Vector3.UnitY);
        var markers = SceneOrientationGizmo.Markers(camera);
        Assert.Equal(6, markers.Length);
        Assert.Equal(3, markers.Count(marker => !marker.Label.StartsWith('−')));
        Assert.Equal(markers.OrderByDescending(marker => marker.Depth), markers);
        foreach (var marker in markers)
            Assert.Equal(marker.Direction, SceneOrientationGizmo.HitTest(camera, marker.Offset));
        Assert.Equal(Vector3.Zero, SceneOrientationGizmo.HitTest(camera, Vector2.Zero));
        Assert.Null(SceneOrientationGizmo.HitTest(camera, new Vector2(100)));
        Assert.Equal(3, SceneOrientationGizmo.Faces(camera).Length);
        camera.LookIn(Vector3.UnitZ, Vector3.UnitY);
        Assert.True(SceneOrientationGizmo.Markers(camera).Single(marker => marker.Direction == Vector3.UnitY)
            .Offset.Y < 0f);
        Assert.Single(SceneOrientationGizmo.Faces(camera));
        Assert.Equal(Vector3.Zero, SceneOrientationGizmo.HitTest(camera, Vector2.Zero));
    }

    /// <summary>Perspective makes far endpoints smaller, while orthographic endpoints keep equal size.</summary>
    [Fact]
    public void EndpointPerspectiveControlsDrawingAndPicking()
    {
        var camera = new EditorCamera();
        camera.LookIn(new Vector3(3, 2, 4), Vector3.UnitY);
        var markers = SceneOrientationGizmo.Markers(camera);
        var near = markers.Single(marker => marker.Direction == -Vector3.UnitY);
        var far = markers.Single(marker => marker.Direction == Vector3.UnitY);
        Assert.True(near.Radius > far.Radius);
        Assert.True(near.Offset.Length() > far.Offset.Length());
        var outside = far.Offset + Vector2.Normalize(far.Offset) * (far.Radius + 3);
        Assert.NotEqual(far.Direction, SceneOrientationGizmo.HitTest(camera, outside));
        var inside = far.Offset + Vector2.Normalize(far.Offset) * (far.Radius + 1);
        Assert.Equal(far.Direction, SceneOrientationGizmo.HitTest(camera, inside));
        camera.IsOrthographic = true;
        markers = SceneOrientationGizmo.Markers(camera);
        Assert.Single(markers.Select(marker => marker.Radius).Distinct());
        Assert.True(SceneOrientationGizmo.Faces(camera)
            .SelectMany(face => new[] { face.A, face.B, face.C, face.D })
            .All(point => point.Length() < 10f));
    }
}
