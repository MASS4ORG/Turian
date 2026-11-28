namespace Gaya.Plugin.Turian;

/// <summary>
/// Where a Scene view's free camera stood, kept while the viewport releases its render service so the view looks the same
/// when it is rebuilt.
/// </summary>
/// <param name="Position">The camera position.</param>
/// <param name="Orientation">The camera orientation.</param>
/// <param name="IsOrthographic">Whether the projection is orthographic.</param>
/// <param name="Frustum">The orthographic half-height.</param>
/// <param name="OrbitPivot">The orbit pivot of the last orbit gesture.</param>
/// <param name="OrbitDistance">The distance to the orbit pivot.</param>
readonly record struct ViewportCameraPose(Vector3 Position, Quaternion Orientation, bool IsOrthographic, float Frustum,
    Vector3 OrbitPivot, float OrbitDistance)
{
    /// <summary>Reads the pose of the camera a controller drives.</summary>
    /// <param name="controller">The camera controller.</param>
    public static ViewportCameraPose Capture(SceneCameraController controller)
    {
        var camera = controller.Camera;
        return new ViewportCameraPose(camera.Position, camera.Orientation, camera.IsOrthographic, camera.Frustum,
            controller.OrbitPivot, controller.OrbitDistance);
    }

    /// <summary>Puts a controller's camera back at this pose.</summary>
    /// <param name="controller">The camera controller.</param>
    public void Apply(SceneCameraController controller)
    {
        var camera = controller.Camera;
        camera.Position = Position;
        camera.Orientation = Orientation;
        camera.IsOrthographic = IsOrthographic;
        camera.Frustum = Frustum;
        controller.OrbitPivot = OrbitPivot;
        controller.OrbitDistance = OrbitDistance;
    }
}
