namespace Turian.Engine.Core;

/// <summary>
/// First-person camera movement. Like <see cref="FreeFlyCameraComponent"/>, but the forward axis
/// is projected onto the XZ plane so the camera does not drift up/down when looking at the sky/ground.
/// Vertical movement (input.Y) is taken in world space.
/// </summary>
[RequireComponent(typeof(CameraComponent))]
[DisallowMultipleComponent]
[ComponentContextMenu("Rendering/Camera/FPS")]
[TypeId("a3000001-0000-4000-8000-000000000005")]
public class FpsCameraComponent : Component
{
    /// <summary>Movement speed in world units per second.</summary>
    public float Speed { get; set; } = 5f;

    /// <summary>Mouse look sensitivity in radians per pixel.</summary>
    public float LookSensitivity { get; set; } = 0.005f;

    /// <summary>
    /// When <c>true</c> (the default), the component reads WASD / Space / Left-Shift for movement
    /// and right-drag for mouse look from the engine <see cref="Input"/> facade every
    /// update, so it drives the camera on its own in a running session. Set <c>false</c> to feed
    /// <see cref="Move"/> / <see cref="Look"/> from your own script instead.
    /// </summary>
    public bool CaptureInput { get; set; } = true;

    /// <inheritdoc/>
    public override void OnUpdate(float deltaTime)
    {
        if (!CaptureInput) return;

        var forward = Axis(Key.W, Key.S);
        var strafe = Axis(Key.D, Key.A);
        var climb = Axis(Key.Space, Key.ShiftLeft);
        if (forward != 0f || strafe != 0f || climb != 0f)
            Move(new Vector3(strafe, climb, forward), deltaTime);

        if (Input.IsMouseButtonDown(MouseButton.Right))
        {
            var delta = Input.MouseDelta;
            if (delta != Vector2.Zero)
                Look(delta.X, delta.Y);
        }

        static float Axis(Key positive, Key negative) =>
            (Input.IsKeyDown(positive) ? 1f : 0f) - (Input.IsKeyDown(negative) ? 1f : 0f);
    }

    /// <summary>
    /// Translates the camera in the XZ ground plane. <paramref name="input"/>:
    /// X = strafe (right), Y = world-up climb, Z = forward (projected on XZ).
    /// </summary>
    public void Move(Vector3 input, float deltaTime)
    {
        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is null) return;

        var forwardXz = new Vector3(camera.Front.X, 0f, camera.Front.Z);
        if (forwardXz.LengthSquared() > 0f)
            forwardXz = Vector3.Normalize(forwardXz);

        var rightXz = new Vector3(camera.Right.X, 0f, camera.Right.Z);
        if (rightXz.LengthSquared() > 0f)
            rightXz = Vector3.Normalize(rightXz);

        var step = Speed * deltaTime;
        camera.Position += rightXz * input.X * step;
        camera.Position += Vector3.UnitY * input.Y * step;
        camera.Position += forwardXz * input.Z * step;
    }

    /// <summary>Rotates the camera by mouse-pixel deltas (same convention as FreeFly).</summary>
    public void Look(float dxPixels, float dyPixels)
    {
        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is null) return;

        camera.Yaw += dxPixels * LookSensitivity;
        camera.Pitch -= dyPixels * LookSensitivity;
    }
}
