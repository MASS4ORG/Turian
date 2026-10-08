namespace Turian.Engine.Core;

/// <summary>
/// Represents a camera component that defines view and projection transformations for rendering.
/// Supports both perspective and orthographic projection modes, physical camera parameters,
/// temporal anti-aliasing jitter, and frustum culling operations.
/// </summary>
[DisallowMultipleComponent]
[ComponentContextMenu("Rendering/Camera")]
[TypeId("1e9919ae-f343-569f-8715-8aa2baab3e39")]
public class CameraComponent : Component, ICamera
{
    /// <summary>The rendering layers included by this camera.</summary>
    public LayerMask CullingMask { get; set; } = LayerMask.Everything;

    const float minNear = 0.0001f;
    const float minFov = 1f * Mathf.DegreesToRadians;
    const float maxFov = 120f * Mathf.DegreesToRadians;

    static Vector3 GlobalUp => Vector3.UnitY;

    float aspect = 1f;
    float pitch;
    float yaw;

    // ortho
    float left = -40;
    float right = 40;
    float bottom = 40;
    float top = -40;

    // jitter in NDC space
    Vector2 jitter;

    // physical camera
    float focalLength = 50f;
    float sensorHeight = 24f;

    // ========= Projection =========

    /// <summary>Gets or sets whether the camera uses perspective projection. When false, orthographic projection is used.</summary>
    public bool UsePerspective
    {
        get => field;
        set
        {
            field = value;
            if (!field) UpdateOrtho();
        }
    } = true;

    /// <summary>Gets or sets the distance to the near clipping plane. Must be greater than 0.0001.</summary>
    public float NearPlane
    {
        get => field;
        set => field = Math.Max(value, minNear);
    } = 0.01f;

    /// <summary>Gets or sets the distance to the far clipping plane. Must be greater than NearPlane + 0.0001.</summary>
    public float FarPlane
    {
        get => field;
        set => field = Math.Max(value, NearPlane + minNear);
    } = 100f;

    /// <inheritdoc/>
    [JsonIgnore]
    [Hide]
    public float FieldOfView
    {
        get => FieldOfViewDegrees * Mathf.DegreesToRadians;
        set => FieldOfViewDegrees = value * Mathf.RadiansToDegrees;
    }

    /// <summary>Gets or sets the vertical field of view in degrees.</summary>
    /// <remarks>Stored in degrees, as saved, so loading and saving a scene never changes the value.</remarks>
    public float FieldOfViewDegrees
    {
        get => field;
        set => field = Math.Clamp(value, minFov * Mathf.RadiansToDegrees, maxFov * Mathf.RadiansToDegrees);
    } = 60f;

    /// <summary>Gets or sets the focal length in millimeters. Affects field of view when using physical camera model.</summary>
    [Hide] // TODO: make it conditional (either physical or FOV)
    [JsonIgnore] // Its setter recomputes FieldOfView, so loading it would overwrite the saved field of view.
    public float FocalLength
    {
        get => focalLength;
        set
        {
            focalLength = Math.Max(1f, value);
            UpdateFovFromPhysical();
        }
    }

    /// <summary>Gets or sets the sensor height in millimeters. Affects field of view when using physical camera model.</summary>
    [Hide] // TODO: make it conditional (either physical or FOV)
    [JsonIgnore] // Its setter recomputes FieldOfView, so loading it would overwrite the saved field of view.
    public float SensorHeight
    {
        get => sensorHeight;
        set
        {
            sensorHeight = Math.Max(1f, value);
            UpdateFovFromPhysical();
        }
    }

    /// <summary>Gets or sets the orthographic frustum size (half-height of the view volume).</summary>
    public float Frustum
    {
        get => field;
        set
        {
            field = Math.Max(value, 0.01f);
            UpdateOrtho();
        }
    } = 40f;

    /// <summary>Gets or sets how the camera determines its aspect ratio.</summary>
    [Hide]
    public AspectMode AspectMode { get; set; } = AspectMode.Viewport;

    /// <summary>Gets or sets the fixed aspect ratio used when AspectMode is Fixed.</summary>
    [Hide]
    public float FixedAspectRatio { get; set; } = 16f / 9f;

    /// <summary>
    /// The aspect ratio last set by <see cref="Resize"/>. Read-only, so a viewer that only wants to
    /// preview this camera — such as the Scene View's camera preview — can size its own render
    /// target from it without ever calling <see cref="Resize"/> itself. Doing so would overwrite
    /// the aspect whichever other consumer (the Game panel, the standalone runtime) actually owns
    /// this camera's viewport currently relies on.
    /// </summary>
    [JsonIgnore]
    [Hide]
    public float AspectRatio => aspect;

    /// <summary>
    /// Gets or sets which camera the runtime renders through when a scene holds several. The
    /// highest value wins; ties go to the first found in hierarchy order. Every camera drawing to
    /// the same target would otherwise overdraw the others, which reads as flicker.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Returns the camera the runtime should render through: the active camera with the highest
    /// <see cref="Priority"/>, or <c>null</c> when the hierarchy holds none.
    /// </summary>
    /// <param name="root">Root of the hierarchy to search.</param>
    public static CameraComponent? FindPrimary(Node? root)
    {
        // A plain scan instead of OrderByDescending: hosts ask for the active camera every frame. The first camera
        // wins a tie, as the stable sort did.
        CameraComponent? primary = null;
        foreach (var camera in Node.GetComponentsInChildren<CameraComponent>(root))
            if (primary is null || camera.Priority > primary.Priority)
                primary = camera;
        return primary;
    }

    // ========= Transform-driven rotation =========

    /// <summary>
    /// Gets or sets upward pitch in radians, clamped to ±89.9°.
    /// Positive pitch maps to negative local X rotation; the node's orientation remains authoritative.
    /// </summary>
    [JsonIgnore]
    [Hide]
    public float Pitch
    {
        get => Node is not null ? -Node.Transform.Rotation.X * Mathf.DegreesToRadians : pitch;
        set
        {
            var clamped = Math.Clamp(value, -Mathf.DegreesToRadians * 89.9f, Mathf.DegreesToRadians * 89.9f);

            if (Node is null)
            {
                pitch = clamped;
                return;
            }

            Node.Rotation = Node.Rotation with { X = -clamped * Mathf.RadiansToDegrees };
        }
    }

    /// <summary>
    /// Gets or sets the camera's yaw rotation (Y-axis) in radians. Mirrors <see cref="Node"/>'s
    /// <c>Transform.Rotation.Y</c> once attached — see <see cref="Pitch"/> for why it isn't
    /// independently serialized.
    /// </summary>
    [JsonIgnore]
    [Hide]
    public float Yaw
    {
        get => Node is not null ? Node.Transform.Rotation.Y * Mathf.DegreesToRadians : yaw;
        set
        {
            if (Node is null)
            {
                yaw = value;
                return;
            }

            Node.Rotation = Node.Rotation with { Y = value * Mathf.RadiansToDegrees };
        }
    }

    // ========= Derived vectors =========

    /// <inheritdoc/>
    [JsonIgnore]
    [Hide]
    public Vector3 Front => Vector3.Transform(Vector3.UnitZ, ViewOrientation);

    /// <inheritdoc/>
    [JsonIgnore]
    [Hide]
    public Vector3 Right => Vector3.Transform(-Vector3.UnitX, ViewOrientation);

    /// <inheritdoc/>
    [JsonIgnore]
    [Hide]
    public Vector3 Up => Vector3.Transform(GlobalUp, ViewOrientation);

    /// <inheritdoc/>
    [JsonIgnore]
    [Hide]
    public Vector3 Position
    {
        get => (Node ?? throw new InvalidOperationException("Camera is not attached to a node.")).GlobalTransform.Position;
        set
        {
            var node = Node ?? throw new InvalidOperationException("Camera is not attached to a node.");
            node.GlobalTransform = node.GlobalTransform with { Position = value };
        }
    }

    Quaternion ViewOrientation => Node?.GlobalTransform.Orientation
        ?? Quaternion.CreateFromYawPitchRoll(yaw, -pitch, 0);

    // ========= Matrices =========

    /// <inheritdoc/>
    public Matrix4x4 GetViewMatrix()
    {
        return Matrix4x4.CreateLookAt(Position, Position + Front, Up);
    }

    /// <inheritdoc/>
    public Matrix4x4 GetProjectionMatrix()
    {
        if (UsePerspective)
        {
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, NearPlane, FarPlane);
            proj.M22 = -proj.M22;

            proj.M31 += jitter.X;
            proj.M32 += jitter.Y;

            return proj;
        }

        return Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, NearPlane, FarPlane);
    }

    /// <inheritdoc/>
    public Matrix4x4 GetInverseViewMatrix()
    {
        _ = Matrix4x4.Invert(GetViewMatrix(), out var ret);
        return ret;
    }

    // ========= Frustum (for culling) =========

    /// <summary>Returns the six frustum planes (left, right, bottom, top, near, far) in world space for culling.</summary>
    public Plane[] GetFrustumPlanes()
    {
        var matrix = GetViewMatrix() * GetProjectionMatrix();
        var planes = new Plane[6];

        planes[0] = Plane.Normalize(new Plane(matrix.M14 + matrix.M11, matrix.M24 + matrix.M21,
            matrix.M34 + matrix.M31, matrix.M44 + matrix.M41)); // left
        planes[1] = Plane.Normalize(new Plane(matrix.M14 - matrix.M11, matrix.M24 - matrix.M21,
            matrix.M34 - matrix.M31, matrix.M44 - matrix.M41)); // right
        planes[2] = Plane.Normalize(new Plane(matrix.M14 + matrix.M12, matrix.M24 + matrix.M22,
            matrix.M34 + matrix.M32, matrix.M44 + matrix.M42)); // bottom
        planes[3] = Plane.Normalize(new Plane(matrix.M14 - matrix.M12, matrix.M24 - matrix.M22,
            matrix.M34 - matrix.M32, matrix.M44 - matrix.M42)); // top
        planes[4] = Plane.Normalize(new Plane(matrix.M13, matrix.M23, matrix.M33, matrix.M43)); // near
        planes[5] = Plane.Normalize(new Plane(matrix.M14 - matrix.M13, matrix.M24 - matrix.M23,
            matrix.M34 - matrix.M33, matrix.M44 - matrix.M43)); // far

        return planes;
    }

    // ========= TAA =========

    /// <summary>Sets the jitter offset in normalized device coordinates (NDC).</summary>
    public void SetJitterNdc(Vector2 jitterNdc)
    {
        jitter = jitterNdc;
    }

    /// <summary>Sets the jitter offset in pixels. Automatically converts to NDC space.</summary>
    public void SetJitterPixels(float x, float y, float width, float height)
    {
        jitter = new Vector2(
            (2f * x) / width,
            (2f * y) / height
        );
    }

    /// <summary>Clears any active jitter offset.</summary>
    public void ClearJitter()
    {
        jitter = default;
    }

    // ========= Utilities =========

    /// <summary>Updates the camera's aspect ratio based on the specified viewport dimensions.</summary>
    public void Resize(uint width, uint height)
    {
        aspect = (float)width / height;
        UpdateOrtho();
    }

    /// <inheritdoc/>
    public Vector2 Project(Vector3 p)
    {
        var v = new Vector4(p.X, p.Y, p.Z, 1f);
        v = Vector4.Transform(v, GetViewMatrix());
        v = Vector4.Transform(v, GetProjectionMatrix());

        if (Math.Abs(v.W) > float.Epsilon)
            v /= v.W;

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

        if (Math.Abs(v.W) > float.Epsilon)
            v /= v.W;

        return new Vector3(v.X, v.Y, v.Z);
    }

    void UpdateOrtho()
    {
        left = -Frustum * aspect * 0.5f;
        right = Frustum * aspect * 0.5f;
        top = -Frustum * 0.5f;
        bottom = Frustum * 0.5f;
    }

    void UpdateFovFromPhysical()
    {
        FieldOfView = 2f * MathF.Atan(sensorHeight / (2f * focalLength));
    }
}

/// <summary>Determines how the camera's aspect ratio is calculated.</summary>
public enum AspectMode
{
    /// <summary>Use the current viewport dimensions.</summary>
    Viewport,

    /// <summary>Use a fixed aspect ratio specified by FixedAspectRatio.</summary>
    Fixed
}
