namespace Turian.Engine.Core;

/// <summary>
/// Editor-only free camera. Lives outside the scene graph — never serialized, never shown in the
/// SceneTree panel. The viewport owns this camera rather than a scene node.
///
/// The engine's world space has +Y pointing down (OBJ/FBX importers negate Y), but the camera
/// convention is Y-up: <see cref="globalUp"/> = +Y, positive pitch = look up. The Vulkan
/// render pass handles the final Y-flip for display.
/// </summary>
public class EditorCamera : ICamera
{
    const float minNear = 0.001f;
    const float minFov = 1f * (MathF.PI / 180f);
    const float maxFov = 120f * (MathF.PI / 180f);

    static readonly Vector3 globalUp = Vector3.UnitY;

    Quaternion orientation = Quaternion.Identity;
    float aspect = 16f / 9f;

    /// <inheritdoc/>
    public Vector3 Position { get; set; } = new(0f, 1f, -5f);

    /// <inheritdoc/>
    public float FieldOfView
    {
        get => field;
        set => field = Math.Clamp(value, minFov, maxFov);
    } = 60f * (MathF.PI / 180f);

    /// <summary>Gets or sets the near clip plane distance.</summary>
    public float NearPlane
    {
        get => field;
        set => field = Math.Max(value, minNear);
    } = 0.01f;

    /// <summary>Gets or sets the far clip plane distance.</summary>
    public float FarPlane { get; set; } = 500f;

    /// <summary>Gets or sets whether the camera uses orthographic projection.</summary>
    public bool IsOrthographic
    {
        get => field;
        set => field = value;
    } = false;

    /// <summary>Gets or sets the orthographic frustum half-height in world units.</summary>
    public float Frustum
    {
        get => field;
        set => field = Math.Max(value, 0.01f);
    } = 10f;

    /// <summary>
    /// The camera's orientation. Rotation is free: the camera can look straight up, straight down
    /// or roll upside down, because viewport drags rotate about its own axes rather than about a
    /// fixed world up.
    /// </summary>
    public Quaternion Orientation
    {
        get => orientation;
        set
        {
            orientation = Quaternion.Normalize(value);
            UpdateVectors();
        }
    }

    /// <summary>
    /// Camera pitch in radians, as an Euler angle about the world X axis. Setting it rebuilds the
    /// orientation from yaw and pitch alone, which discards any roll.
    /// </summary>
    public float Pitch
    {
        get => MathF.Asin(Math.Clamp(Front.Y, -1f, 1f));
        set => SetYawPitch(Yaw, value);
    }

    /// <summary>
    /// Camera yaw in radians, as an Euler angle about the world Y axis. Setting it rebuilds the
    /// orientation from yaw and pitch alone, which discards any roll.
    /// </summary>
    public float Yaw
    {
        get => MathF.Atan2(Front.X, Front.Z);
        set => SetYawPitch(value, Pitch);
    }

    /// <summary>
    /// Points the camera using world-space yaw and pitch, levelling any roll. This is the entry
    /// point for callers that think in Euler angles — serialized cameras and the screenshot CLI.
    /// </summary>
    /// <param name="yawRadians">Yaw about the world Y axis.</param>
    /// <param name="pitchRadians">Pitch about the camera's right axis, clamped just short of vertical.</param>
    public void SetYawPitch(float yawRadians, float pitchRadians)
    {
        // Euler input cannot express "straight up", so clamp just short of it. Drag-driven rotation
        // goes through RotateLocal instead and has no such limit.
        var limit = 89.99f * (MathF.PI / 180f);
        var clampedPitch = Math.Clamp(pitchRadians, -limit, limit);

        var front = Vector3.Normalize(new Vector3(
            MathF.Sin(yawRadians) * MathF.Cos(clampedPitch),
            MathF.Sin(clampedPitch),
            MathF.Cos(yawRadians) * MathF.Cos(clampedPitch)));

        LookIn(front, globalUp);
    }

    /// <summary>
    /// Rotates the camera about its own axes. Horizontal drags therefore move the view along the
    /// screen's horizontal instead of sweeping a cone around the world up axis, and vertical drags
    /// pass over the top instead of stopping at it.
    /// </summary>
    /// <param name="yawRadians">Rotation about the camera's own up axis; positive turns right.</param>
    /// <param name="pitchRadians">Rotation about the camera's own right axis; positive tilts up.</param>
    public void RotateLocal(float yawRadians, float pitchRadians)
    {
        // Yaw is negated because Right is screen-right, which is cross(Front, Up) — the opposite
        // hand to Up. A positive right-hand-rule turn about Up therefore swings the view left.
        var yawRotation = Quaternion.CreateFromAxisAngle(Up, -yawRadians);
        var pitchRotation = Quaternion.CreateFromAxisAngle(Right, pitchRadians);
        Orientation = pitchRotation * yawRotation * orientation;
    }

    /// <summary>
    /// Rolls the camera about its own forward axis.
    /// </summary>
    /// <param name="radians">Rotation about the view direction; positive rolls clockwise.</param>
    public void RollLocal(float radians) =>
        Orientation = Quaternion.CreateFromAxisAngle(Front, radians) * orientation;

    /// <summary>
    /// Aims the camera along <paramref name="front"/>, using <paramref name="up"/> as the reference
    /// for which way is up. Falls back to the current up when the two are parallel.
    /// </summary>
    /// <param name="front">The direction to look along.</param>
    /// <param name="up">Reference up direction.</param>
    public void LookIn(Vector3 front, Vector3 up)
    {
        if (front.LengthSquared() < 1e-8f) return;

        var f = Vector3.Normalize(front);
        var reference = Vector3.Cross(f, up).LengthSquared() < 1e-6f ? Up : up;

        // Matches GetViewMatrix: screen-right is cross(Front, Up) for Silk's right-handed look-at.
        var r = Vector3.Normalize(Vector3.Cross(f, reference));
        var u = Vector3.Normalize(Vector3.Cross(r, f));

        Orientation = QuaternionFromBasis(f, u);
    }

    /// <inheritdoc/>
    public Vector3 Front { get; private set; } = Vector3.UnitZ;

    /// <inheritdoc/>
    public Vector3 Right { get; private set; } = Vector3.UnitX;

    /// <inheritdoc/>
    public Vector3 Up { get; private set; } = Vector3.UnitY;

    /// <summary>Initializes and computes initial direction vectors.</summary>
    public EditorCamera() => UpdateVectors();

    /// <summary>Levels the camera so its up axis points along world up, keeping the view direction.</summary>
    public void LevelRoll() => LookIn(Front, globalUp);

    /// <summary>Updates the aspect ratio when the viewport is resized.</summary>
    public void Resize(uint width, uint height)
    {
        if (height == 0) return;
        aspect = (float)width / height;
    }

    /// <inheritdoc/>
    public Matrix4x4 GetViewMatrix() =>
        Matrix4x4.CreateLookAt(Position, Position + Front, Up);

    /// <inheritdoc/>
    public Matrix4x4 GetProjectionMatrix()
    {
        if (IsOrthographic)
        {
            var halfH = Frustum;
            var halfW = halfH * aspect;
            return Matrix4x4.CreateOrthographicOffCenter(-halfW, halfW, halfH, -halfH, NearPlane, FarPlane);
        }

        return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, NearPlane, FarPlane);
    }

    /// <inheritdoc/>
    public Matrix4x4 GetInverseViewMatrix()
    {
        _ = Matrix4x4.Invert(GetViewMatrix(), out var inv);
        return inv;
    }

    /// <inheritdoc/>
    public Vector2 Project(Vector3 p)
    {
        var v = new Vector4(p.X, p.Y, p.Z, 1f);
        v = Vector4.Transform(v, GetViewMatrix());
        v = Vector4.Transform(v, GetProjectionMatrix());
        if (MathF.Abs(v.W) > float.Epsilon) v /= v.W;
        return new Vector2(v.X, v.Y);
    }

    /// <inheritdoc/>
    public Vector3 UnProject(Vector2 p)
    {
        var v = new Vector4(p.X, p.Y, 0f, 1f);
        _ = Matrix4x4.Invert(GetProjectionMatrix(), out var projInv);
        _ = Matrix4x4.Invert(GetViewMatrix(), out var viewInv);
        v = Vector4.Transform(v, projInv);
        v = Vector4.Transform(v, viewInv);
        if (MathF.Abs(v.W) > float.Epsilon) v /= v.W;
        return new Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>Toggles between perspective and orthographic projection.</summary>
    public void ToggleProjection() => IsOrthographic = !IsOrthographic;

    void UpdateVectors()
    {
        // Identity orientation looks along +Z with +Y up, matching the previous yaw=0/pitch=0 basis.
        Front = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, orientation));
        Up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, orientation));

        // Screen-right, not a model axis: Silk's right-handed CreateLookAt puts cross(Front, Up)
        // on the right of the image. Deriving it keeps Right correct even when the camera is rolled.
        Right = Vector3.Normalize(Vector3.Cross(Front, Up));
    }

    /// <summary>
    /// Builds the orientation whose forward is <paramref name="front"/> and whose up is
    /// <paramref name="up"/>, both assumed normalized and perpendicular.
    /// </summary>
    static Quaternion QuaternionFromBasis(Vector3 front, Vector3 up)
    {
        var right = Vector3.Cross(up, front);
        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            front.X, front.Y, front.Z, 0f,
            0f, 0f, 0f, 1f);

        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
    }
}
