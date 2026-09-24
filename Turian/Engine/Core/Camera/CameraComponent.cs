namespace Turian.Engine.Core;

/// <summary>
/// Represents a camera component that defines view and projection transformations for rendering.
/// Supports both perspective and orthographic projection modes, physical camera parameters,
/// temporal anti-aliasing jitter, and frustum culling operations.
/// </summary>
/// TODO: implement conditional Inspector fields (ShowIfAttribute)
[DisallowMultipleComponent]
[ComponentContextMenu("Rendering/Camera")]
[TypeId("a3000001-0000-4000-8000-000000000001")]
public class CameraComponent : Component, ICamera
{
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
    [HideInEditor]
    public float FieldOfView
    {
        get => field;
        set => field = Math.Clamp(value, minFov, maxFov);
    } = 60f * Mathf.DegreesToRadians;

    /// <summary>Gets or sets the vertical field of view in degrees.</summary>
    public float FieldOfViewDegrees
    {
        get => FieldOfView * Mathf.RadiansToDegrees;
        set => FieldOfView = value * Mathf.DegreesToRadians;
    }

    /// <summary>Gets or sets the focal length in millimeters. Affects field of view when using physical camera model.</summary>
    [HideInEditor] // TODO: make it conditional (either physical or FOV)
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
    [HideInEditor] // TODO: make it conditional (either physical or FOV)
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
    [HideInEditor]
    public AspectMode AspectMode { get; set; } = AspectMode.Viewport;

    /// <summary>Gets or sets the fixed aspect ratio used when AspectMode is Fixed.</summary>
    [HideInEditor]
    public float FixedAspectRatio { get; set; } = 16f / 9f;

    /// <summary>
    /// The aspect ratio last set by <see cref="Resize"/>. Read-only, so a viewer that only wants to
    /// preview this camera — such as the Scene View's camera preview — can size its own render
    /// target from it without ever calling <see cref="Resize"/> itself. Doing so would overwrite
    /// the aspect whichever other consumer (the Game panel, the standalone runtime) actually owns
    /// this camera's viewport currently relies on.
    /// </summary>
    [JsonIgnore]
    [HideInEditor]
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
    public static CameraComponent? FindPrimary(Node? root) =>
        Node.GetComponentsInChildren<CameraComponent>(root)
            .OrderByDescending(static camera => camera.Priority)
            .FirstOrDefault();

    // ========= Transform-driven rotation =========

    /// <summary>
    /// Gets or sets the camera's pitch rotation (X-axis) in radians. Clamped to ±89.9° to prevent
    /// gimbal lock. Mirrors <see cref="Node"/>'s <c>Transform.Rotation.X</c> once attached — not
    /// independently serialized, like <see cref="Position"/>, so a scene file's authoritative
    /// <c>Transform.Orientation</c> is never second-guessed by a redundant scalar copy.
    /// </summary>
    [JsonIgnore]
    [HideInEditor]
    public float Pitch
    {
        // Transform.Rotation is Euler degrees (Transform.cs: Orientation.ToEulerDegrees()); this
        // property's contract — and every caller's — is radians, so the conversion happens here
        // rather than leaking degrees into a "radians" name.
        get => Node is not null ? Node.Transform.Rotation.X * Mathf.DegreesToRadians : pitch;
        set
        {
            var clamped = Math.Clamp(value, -Mathf.DegreesToRadians * 89.9f, Mathf.DegreesToRadians * 89.9f);

            if (Node is null)
            {
                pitch = clamped;
                return;
            }

            Node.Transform.Rotation = Node.Transform.Rotation with { X = clamped * Mathf.RadiansToDegrees };
            UpdateVectors();
        }
    }

    /// <summary>
    /// Gets or sets the camera's yaw rotation (Y-axis) in radians. Mirrors <see cref="Node"/>'s
    /// <c>Transform.Rotation.Y</c> once attached — see <see cref="Pitch"/> for why it isn't
    /// independently serialized.
    /// </summary>
    [JsonIgnore]
    [HideInEditor]
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

            Node.Transform.Rotation = Node.Transform.Rotation with { Y = value * Mathf.RadiansToDegrees };
            UpdateVectors();
        }
    }

    // ========= Derived vectors =========

    /// <inheritdoc/>
    [JsonIgnore]
    [HideInEditor]
    public Vector3 Front { get; private set; } = Vector3.UnitZ;

    /// <inheritdoc/>
    [JsonIgnore]
    [HideInEditor]
    public Vector3 Right { get; private set; } = Vector3.UnitX;

    /// <inheritdoc/>
    [JsonIgnore]
    [HideInEditor]
    public Vector3 Up { get; private set; } = GlobalUp;

    /// <inheritdoc/>
    [JsonIgnore]
    [HideInEditor]
    public Vector3 Position
    {
        get => (Node ?? throw new InvalidOperationException("Camera is not attached to a node.")).Transform.Position;
        set => (Node ?? throw new InvalidOperationException("Camera is not attached to a node.")).Transform.Position = value;
    }

    // ========= Lifecycle =========

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately does not push <see cref="Pitch"/>/<see cref="Yaw"/>'s pre-attach staging
    /// fields onto <see cref="Node"/>'s <c>Transform.Rotation</c>: the Transform is already the
    /// authoritative source (set directly by scene deserialization, the Inspector, or a script),
    /// and overwriting it here previously clobbered a correctly-authored orientation whenever
    /// those staging fields held anything else — as little as a units mismatch between a
    /// hand-authored scene file's redundant Pitch/Yaw scalars and this property's radians
    /// contract was enough to point a camera at the sky. This only recomputes the derived
    /// <see cref="Front"/>/<see cref="Right"/>/<see cref="Up"/> vectors from whatever orientation
    /// the Transform already carries.
    /// </remarks>
    public override void OnAttached() => UpdateVectors();

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

    void UpdateVectors()
    {
        var currentPitch = Pitch;
        var currentYaw = Yaw;

        // Convention: yaw=0, pitch=0 → Front = (0, 0, +1).
        // yaw rotates clockwise around +Y when viewed from above; positive pitch tilts up.
        Front = Vector3.Normalize(new Vector3
        {
            X = MathF.Sin(currentYaw) * MathF.Cos(currentPitch),
            Y = MathF.Sin(currentPitch),
            Z = MathF.Cos(currentYaw) * MathF.Cos(currentPitch)
        });

        Right = Vector3.Normalize(Vector3.Cross(Front, GlobalUp));
        Up = Vector3.Normalize(Vector3.Cross(Right, Front));
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
