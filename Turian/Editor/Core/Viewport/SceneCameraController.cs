namespace Turian.Editor.Core;

/// <summary>
/// Processes viewport input and drives an <see cref="EditorCamera"/> through four navigation
/// modes that match Unity/Godot conventions:
///
///   • <b>Fly</b> — right-click drag looks around; WASD/QE move (Q/E = world Y).
///   • <b>Orbit</b> — Alt + left-click drag rotates around <see cref="orbitPivot"/>.
///   • <b>Pan</b> — middle-click drag translates the camera on its local XZ plane.
///   • <b>Zoom</b> — scroll wheel dollies along <see cref="EditorCamera.Front"/>.
///
/// This class is UI-framework-agnostic — it receives abstract input events and mutates the
/// camera. The Studio's <c>SceneViewerControl</c> translates Avalonia events into these calls.
/// </summary>
public sealed class SceneCameraController
{
    const float fastMoveMultiplier = 4f;

    NavigationMode mode;
    float lastMouseX;
    float lastMouseY;

    // Orbit state
    Vector3 orbitPivot;
    float orbitDistance = 5f;

    /// <summary>Gets the camera being controlled.</summary>
    public EditorCamera Camera { get; }

    /// <summary>Gets or sets whether the Shift key is held (4× speed).</summary>
    public bool IsFast { get; set; }

    /// <summary>Gets or sets whether the Alt key is held.</summary>
    public bool IsAlt { get; set; }

    /// <summary>Gets or sets whether the left mouse button is held.</summary>
    public bool IsLeftButton { get; set; }

    /// <summary>Gets or sets whether the middle mouse button is held.</summary>
    public bool IsMiddleButton { get; set; }

    /// <summary>Gets or sets whether the right mouse button is held.</summary>
    public bool IsRightButton { get; set; }

    /// <summary>Gets the current navigation mode.</summary>
    public NavigationMode Mode => mode;

    /// <summary>Metres per second the camera flies at, before the Shift multiplier.</summary>
    public float MoveSpeed { get; set; } = 5f;

    /// <summary>Radians of rotation per pixel of pointer travel.</summary>
    public float LookSensitivity { get; set; } = 0.005f;

    /// <summary>Fraction of <see cref="MoveSpeed"/> one scroll notch dollies by.</summary>
    public float ZoomFraction { get; set; } = 0.1f;

    /// <summary>Gets or sets the orbit pivot point in world space.</summary>
    public Vector3 OrbitPivot
    {
        get => orbitPivot;
        set => orbitPivot = value;
    }

    /// <summary>Gets or sets the distance from the orbit pivot.</summary>
    public float OrbitDistance
    {
        get => orbitDistance;
        set => orbitDistance = MathF.Max(value, 0.01f);
    }

    /// <summary>Creates a controller that drives the given camera.</summary>
    public SceneCameraController(EditorCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Camera = camera;
    }

    /// <summary>
    /// Notifies the controller that a mouse button was pressed. Call this on pointer-down.
    /// </summary>
    /// <param name="x">Pointer X in viewport pixels.</param>
    /// <param name="y">Pointer Y in viewport pixels.</param>
    public void OnMouseDown(float x, float y)
    {
        lastMouseX = x;
        lastMouseY = y;

        if (IsRightButton)
        {
            mode = NavigationMode.Fly;
        }
        else if (IsLeftButton && IsAlt)
        {
            mode = NavigationMode.Orbit;
            orbitPivot = Camera.Position + Camera.Front * orbitDistance;
        }
        else if (IsMiddleButton)
        {
            mode = NavigationMode.Pan;
        }
    }

    /// <summary>
    /// Notifies the controller that a mouse button was released. When all tracked buttons
    /// are up the mode resets to <see cref="NavigationMode.None"/>.
    /// </summary>
    public void OnMouseUp()
    {
        if (!IsLeftButton && !IsMiddleButton && !IsRightButton)
            mode = NavigationMode.None;
    }

    /// <summary>
    /// Notifies the controller that the pointer moved. Delegates to the active navigation mode.
    /// </summary>
    public void OnMouseMove(float x, float y)
    {
        var dx = x - lastMouseX;
        var dy = y - lastMouseY;
        lastMouseX = x;
        lastMouseY = y;

        switch (mode)
        {
            case NavigationMode.Fly:
                FlyLook(dx, dy);
                break;
            case NavigationMode.Orbit:
                OrbitLook(dx, dy);
                break;
            case NavigationMode.Pan:
                PanMove(dx, dy);
                break;
        }
    }

    /// <summary>
    /// Processes a scroll-wheel event. Positive <paramref name="delta"/> zooms in (dollies forward);
    /// negative zooms out.
    /// </summary>
    public void OnWheel(float delta)
    {
        if (mode == NavigationMode.Orbit)
        {
            orbitDistance *= 1f - delta * ZoomFraction;
            orbitDistance = MathF.Max(orbitDistance, 0.01f);
            Camera.Position = orbitPivot - Camera.Front * orbitDistance;
        }
        else
        {
            var step = MoveSpeed * delta * ZoomFraction;
            var distance = Camera.Position.Length();
            if (distance > 0.01f)
                step = MathF.Min(step, distance * 0.5f);
            Camera.Position += Camera.Front * step;
        }
    }

    /// <summary>
    /// Applies WASD/QE keyboard movement for the current frame. Call once per frame with the
    /// elapsed time. The <paramref name="keys"/> set should contain the currently-held keys.
    /// </summary>
    /// <param name="keys">Set of held key codes (engine key enum values as integers).</param>
    /// <param name="dt">Delta time in seconds.</param>
    /// <param name="moveForward">Key code for move-forward.</param>
    /// <param name="moveBackward">Key code for move-backward.</param>
    /// <param name="moveLeft">Key code for strafe-left.</param>
    /// <param name="moveRight">Key code for strafe-right.</param>
    /// <param name="moveUp">Key code for move-up along the camera's own up axis (Q).</param>
    /// <param name="moveDown">Key code for move-down along the camera's own up axis (E).</param>
    public void ApplyKeyboardMovement(
        HashSet<int> keys, float dt,
        int moveForward, int moveBackward,
        int moveLeft, int moveRight,
        int moveUp, int moveDown)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return;

        var x = 0f;
        var y = 0f;
        var z = 0f;

        if (keys.Contains(moveRight)) x += 1f;
        if (keys.Contains(moveLeft)) x -= 1f;
        if (keys.Contains(moveUp)) y += 1f;
        if (keys.Contains(moveDown)) y -= 1f;
        if (keys.Contains(moveForward)) z += 1f;
        if (keys.Contains(moveBackward)) z -= 1f;

        if (x == 0f && y == 0f && z == 0f) return;

        var multiplier = IsFast ? fastMoveMultiplier : 1f;
        var step = MoveSpeed * multiplier * dt;

        // Every axis is camera-local, Q/E included: with free rotation the camera can be rolled or
        // upside down, and a world-Y lift would then send it sideways relative to what you see.
        if (x != 0f || z != 0f)
        {
            Camera.Position += Camera.Right * x * step;
            Camera.Position += Camera.Front * z * step;
        }

        if (y != 0f)
            Camera.Position += Camera.Up * y * step;
    }

    /// <summary>
    /// Moves the camera to frame a node at a fixed distance along the current view direction.
    /// </summary>
    public void FrameNode(Node target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var distance = 5f;
        Camera.Position = target.GlobalTransform.Position - Camera.Front * distance;
        orbitDistance = distance;
        orbitPivot = target.GlobalTransform.Position;
    }

    /// <summary>
    /// Moves the camera to frame the given world-space bounds.
    /// </summary>
    public void FrameBounds(Bounds bounds)
    {
        if (bounds.IsEmpty) return;

        var radius = bounds.Size.Length() * 0.5f;
        if (radius < 0.001f) radius = 1f;
        var distance = radius / MathF.Tan(Camera.FieldOfView * 0.5f);
        Camera.Position = bounds.Center - Camera.Front * distance;
        orbitDistance = distance;
        orbitPivot = bounds.Center;
        Camera.FarPlane = MathF.Max(Camera.FarPlane, (distance + radius * 2f) * 1.5f);
    }

    // Rotation is about the camera's own axes, so a horizontal drag moves the view along the
    // screen's horizontal instead of sweeping a cone around world up, and a vertical drag carries
    // straight over the top. Screen Y grows downward, hence the negated dy.
    void FlyLook(float dx, float dy) =>
        Camera.RotateLocal(dx * LookSensitivity, -dy * LookSensitivity);

    void OrbitLook(float dx, float dy)
    {
        Camera.RotateLocal(dx * LookSensitivity, -dy * LookSensitivity);
        Camera.Position = orbitPivot - Camera.Front * orbitDistance;
    }

    void PanMove(float dx, float dy)
    {
        var panScale = MoveSpeed * 0.002f;
        var panX = -(dx * panScale);
        var panY = -(dy * panScale);

        Camera.Position += Camera.Right * panX;
        Camera.Position += Camera.Up * panY;
    }
}

/// <summary>Navigation mode for the scene viewport camera.</summary>
public enum NavigationMode
{
    /// <summary>No navigation active.</summary>
    None,

    /// <summary>Free-fly mode (right-click drag + WASD/QE).</summary>
    Fly,

    /// <summary>Orbit mode (Alt + left-click drag).</summary>
    Orbit,

    /// <summary>Pan mode (middle-click drag).</summary>
    Pan
}
