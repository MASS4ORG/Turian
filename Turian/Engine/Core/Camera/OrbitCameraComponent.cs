namespace Turian.Engine.Core;

/// <summary>
/// Orbits the camera around a fixed point at a fixed distance. Mouse deltas update yaw/pitch and
/// the camera position is recomputed each call so it remains <see cref="Distance"/> units behind
/// <see cref="Target"/> along the camera's Front axis.
/// </summary>
[RequireComponent(typeof(CameraComponent))]
[DisallowMultipleComponent]
[ComponentContextMenu("Rendering/Camera/Orbit")]
[TypeId("a3000001-0000-4000-8000-000000000003")]
[PublicAPI]
public class OrbitCameraComponent : Component
{
    /// <summary>World-space point the camera orbits around.</summary>
    public Vector3 Target { get; set; }

    /// <summary>Distance from <see cref="Target"/> along the camera's Front axis.</summary>
    public float Distance
    {
        get => field;
        set => field = MathF.Max(value, 0.01f);
    } = 5f;

    /// <summary>Mouse look sensitivity in radians per pixel.</summary>
    public float LookSensitivity { get; set; } = 0.005f;

    /// <summary>Distance change per scroll unit (negative = zoom in).</summary>
    public float ZoomStep { get; set; } = 0.5f;

    /// <summary>Updates yaw/pitch by mouse-pixel deltas and re-snaps the camera to its orbit position.</summary>
    public void Orbit(float dxPixels, float dyPixels)
    {
        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is null) return;

        camera.Yaw += dxPixels * LookSensitivity;
        camera.Pitch -= dyPixels * LookSensitivity;
        SnapToOrbit(camera);
    }

    /// <summary>Adjusts <see cref="Distance"/> by a scroll amount and re-snaps.</summary>
    public void Zoom(float scrollDelta)
    {
        Distance -= scrollDelta * ZoomStep;
        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is not null) SnapToOrbit(camera);
    }

    /// <inheritdoc/>
    public override void OnLateUpdate(float deltaTime)
    {
        // Re-snap each frame so external Target changes (e.g. follow a moving point) are tracked.
        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is not null) SnapToOrbit(camera);
    }

    void SnapToOrbit(CameraComponent camera)
    {
        camera.Position = Target - camera.Front * Distance;
    }
}
