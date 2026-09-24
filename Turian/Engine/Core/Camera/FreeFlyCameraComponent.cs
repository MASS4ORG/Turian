namespace Turian.Engine.Core;

/// <summary>
/// Translates the camera in its local frame: input X moves along Right, Y along Up, Z along Front.
/// Also exposes <see cref="Look"/> for mouse-driven yaw/pitch updates. Movement is unconstrained on Y
/// (true 6DoF fly cam) — for ground-locked movement, use <see cref="FpsCameraComponent"/>.
/// </summary>
[RequireComponent(typeof(CameraComponent))]
[DisallowMultipleComponent]
[ComponentContextMenu("Rendering/Camera/Free Fly")]
[TypeId("a3000001-0000-4000-8000-000000000004")]
public class FreeFlyCameraComponent : Component
{
    /// <summary>Movement speed in world units per second.</summary>
    public float Speed { get; set; } = 10f;

    /// <summary>Mouse look sensitivity in radians per pixel.</summary>
    public float LookSensitivity { get; set; } = 0.005f;

    /// <summary>
    /// Translates the camera. <paramref name="input"/> is a per-axis amount in [-1, 1]:
    /// X = right, Y = up, Z = forward.
    /// </summary>
    public void Move(Vector3 input, float deltaTime)
    {
        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is null) return;

        var step = Speed * deltaTime;
        camera.Position += camera.Right * input.X * step;
        camera.Position += camera.Up * input.Y * step;
        camera.Position += camera.Front * input.Z * step;
    }

    /// <summary>
    /// Rotates the camera by mouse-pixel deltas. Positive <paramref name="dxPixels"/> turns right,
    /// positive <paramref name="dyPixels"/> looks down (screen Y).
    /// </summary>
    public void Look(float dxPixels, float dyPixels)
    {
        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is null) return;

        camera.Yaw += dxPixels * LookSensitivity;
        camera.Pitch -= dyPixels * LookSensitivity;
    }
}
