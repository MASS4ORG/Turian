namespace Turian.Editor.Core;

/// <summary>Draws and edits scene transforms using overlay handles in world or local space.</summary>
public sealed partial class TransformGizmo
{
    const float handleThickness = 4f;
    const float hitThresholdPx = 10f;
    const float centerHitRadiusPx = 9f;
    const float handleLengthPixels = 90f;
    const float combinedScaleLength = 1.5f;
    const float rotationRadius = 0.85f;
    const int arcSegments = 64;

    Vector4 XColor => Settings.AxisColor(Vector3.UnitX);
    Vector4 YColor => Settings.AxisColor(Vector3.UnitY);
    Vector4 ZColor => Settings.AxisColor(Vector3.UnitZ);

    TransformGizmoAxis axis;
    TransformGizmoMode handleMode;
    bool isDragging;
    Vector2 pointerScreen;
    Vector2 dragStartScreen;
    Vector2 scaleScreenDirection;
    Vector3 axisStartAnchorWorld;
    Vector3 axisStartPointerWorld;
    Vector3 dragX;
    Vector3 dragY;
    Vector3 dragZ;
    TransformSelection? transformSelection;

    /// <summary>Gets or sets the color of the handle under the pointer; the host sets it from the theme.</summary>
    public Vector4 HoverColor { get; set; } = new(1f, 0.87f, 0.52f, 1f);

    /// <summary>Gets or sets the color of the free-move center handle.</summary>
    public Vector4 CenterColor { get; set; } = new(0.86f, 0.89f, 0.95f, 1f);

    /// <summary>Gets or sets the color of the handle being dragged.</summary>
    public Vector4 DragColor { get; set; } = Vector4.One;

    /// <summary>Gets or sets the displayed tool, including the combined transform tool.</summary>
    public TransformGizmoMode Mode { get; set; } = TransformGizmoMode.Translate;

    /// <summary>Gets or sets whether the gizmo uses world or local axes.</summary>
    public TransformGizmoSpace Space { get; set; } = TransformGizmoSpace.World;

    /// <summary>Gets or sets the shared Scene view gizmo colors and visibility.</summary>
    public SceneGizmoSettings Settings { get; set; } = new();

    /// <summary>Gets or sets whether snapping applies, retaining the individual snap intervals.</summary>
    public bool SnapEnabled { get; set; } = false;

    /// <summary>Gets or sets the translation snap interval. Zero disables snapping.</summary>
    public float SnapTranslation { get; set; } = 1f;

    /// <summary>Gets or sets the rotation snap angle in degrees. Zero disables snapping.</summary>
    public float SnapRotation { get; set; } = 15f;

    /// <summary>Gets or sets the scale snap interval. Zero disables snapping.</summary>
    public float SnapScale { get; set; } = 0.1f;

    /// <summary>Gets or sets the node currently targeted by the gizmo. Null hides the gizmo.</summary>
    public Node? SelectedNode { get; set; }

    /// <summary>The selected objects transformed together; an empty set uses SelectedNode alone.</summary>
    public IReadOnlyList<Node> SelectedNodes { get; set; } = [];

    /// <summary>Whether the shared pivot uses the selection's centre instead of the active object.</summary>
    public bool CenterPivot { get; set; }

    /// <summary>The shared world position displayed by the handles.</summary>
    public Vector3 PivotPosition => TransformSelection.GetPivot(TargetNodes, SelectedNode, CenterPivot);

    IReadOnlyList<Node> TargetNodes => SelectedNodes.Count > 0 ? SelectedNodes
        : SelectedNode is { } node ? [node] : [];

    /// <summary>The Scene view layer locks respected by every transform gesture.</summary>
    public SceneViewSettings ViewSettings { get; set; } = new();

    /// <summary>Gets the axis currently highlighted or being dragged.</summary>
    public TransformGizmoAxis Axis => axis;

    /// <summary>Gets the operation of the hovered or dragged handle, including in combined mode.</summary>
    public TransformGizmoMode HandleMode => handleMode;

    /// <summary>Gets whether a transform gesture is active.</summary>
    public bool IsDragging => isDragging;

    bool CanInteract => SelectedNode is not null && ViewSettings.CanSelect(SelectedNode)
                        && Settings.Visible && Mode != TransformGizmoMode.Select;

    /// <summary>Gets the snapped rotation angle of the active gesture in degrees.</summary>
    public float RotationDegrees => AppliedRotationAngle * 180f / MathF.PI;

    /// <summary>Raised once when a drag operation begins.</summary>
    public event Action? DragStarted;

    /// <summary>Raised once when a drag operation ends.</summary>
    public event Action? DragEnded;

    /// <summary>Raised after a drag updates the selected transform.</summary>
    public event Action? TransformEdited;

    /// <summary>Begins dragging the handle beneath a viewport pointer.</summary>
    public void ProcessPointerDown(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!CanInteract || isDragging) return;
        var hit = HitTest(screenPos, camera, viewportSize);
        if (hit.Axis == TransformGizmoAxis.None) return;

        axis = hit.Axis;
        handleMode = hit.Mode;
        isDragging = true;
        pointerScreen = dragStartScreen = screenPos;
        axisStartAnchorWorld = PivotPosition;
        transformSelection = new TransformSelection(TargetNodes.Where(ViewSettings.CanSelect), axisStartAnchorWorld,
            Space == TransformGizmoSpace.Local ? SelectedNode!.GlobalTransform.Orientation : Quaternion.Identity);
        (dragX, dragY, dragZ) = GetAxes();
        axisStartPointerWorld = IntersectScreenPlane(screenPos, camera, viewportSize, axisStartAnchorWorld)
            ?? axisStartAnchorWorld;
        InitializeScaleDirection(camera, viewportSize);
        if (handleMode == TransformGizmoMode.Rotate) BeginRotation(screenPos, camera, viewportSize);
        DragStarted?.Invoke();
    }

    void InitializeScaleDirection(ICamera camera, Vector2 viewportSize)
    {
        var scale = ComputeGizmoScale(camera, axisStartAnchorWorld, viewportSize);
        var sign = Mode == TransformGizmoMode.Combined && handleMode == TransformGizmoMode.Scale
            && axis is TransformGizmoAxis.Xy or TransformGizmoAxis.Xz or TransformGizmoAxis.Yz ? -1f : 1f;
        scaleScreenDirection = WorldToPixel(axisStartAnchorWorld + HandleDirection() * scale * sign,
            camera, viewportSize) - WorldToPixel(axisStartAnchorWorld, camera, viewportSize);
    }

    bool Displays(TransformGizmoMode operation) => Mode == operation || Mode == TransformGizmoMode.Combined;

    /// <summary>Updates an active drag or highlights the handle beneath the pointer.</summary>
    public void ProcessPointerMove(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        pointerScreen = screenPos;
        if (!CanInteract)
        {
            ProcessPointerUp();
            ClearHover();
            return;
        }
        if (!isDragging)
        {
            var hit = HitTest(screenPos, camera, viewportSize);
            axis = hit.Axis;
            handleMode = hit.Mode;
            return;
        }

        switch (handleMode)
        {
            case TransformGizmoMode.Rotate: ApplyRotation(screenPos, camera, viewportSize); break;
            case TransformGizmoMode.Scale: ApplyScale(screenPos); break;
            case TransformGizmoMode.Translate:
                if (IntersectScreenPlane(screenPos, camera, viewportSize, axisStartAnchorWorld) is { } point)
                    ApplyTranslation(point);
                break;
        }
    }

    /// <summary>Ends the active drag and clears its highlight.</summary>
    public void ProcessPointerUp()
    {
        if (!isDragging) return;
        isDragging = false;
        axis = TransformGizmoAxis.None;
        DragEnded?.Invoke();
    }

    /// <summary>Clears a hover highlight when the pointer leaves the viewport.</summary>
    public void ClearHover()
    {
        if (!isDragging) axis = TransformGizmoAxis.None;
    }

    void ApplyTranslation(Vector3 hitWorld)
    {
        var delta = hitWorld - axisStartPointerWorld;
        if (axis != TransformGizmoAxis.Center)
        {
            var mask = AxisMask(axis);
            delta = dragX * SnapDistance(Vector3.Dot(delta, dragX)) * mask.X
                + dragY * SnapDistance(Vector3.Dot(delta, dragY)) * mask.Y
                + dragZ * SnapDistance(Vector3.Dot(delta, dragZ)) * mask.Z;
        }
        transformSelection!.Translate(delta);
        TransformEdited?.Invoke();
    }

    float SnapDistance(float distance) =>
        SnapEnabled && SnapTranslation > 0f ? SnapValue(distance, SnapTranslation) : distance;

    void ApplyScale(Vector2 screenPos)
    {
        var delta = screenPos - dragStartScreen;
        var amount = axis == TransformGizmoAxis.Center ? (delta.X - delta.Y) * 0.005f : ScaleTravel(delta);
        var factor = 1f + amount;
        if (SnapEnabled && SnapScale > 0f) factor = SnapValue(factor, SnapScale);
        factor = MathF.Max(factor, 0.01f);
        var mask = AxisMask(axis);
        transformSelection!.Scale(Vector3.One + mask * (factor - 1f));
        TransformEdited?.Invoke();
    }

    float ScaleTravel(Vector2 delta)
    {
        var lengthSquared = scaleScreenDirection.LengthSquared();
        return lengthSquared < 1e-6f ? 0f : Vector2.Dot(delta, scaleScreenDirection) / lengthSquared;
    }

    Vector3 HandleDirection()
    {
        var mask = AxisMask(axis);
        return dragX * mask.X + dragY * mask.Y + dragZ * mask.Z;
    }

    static Vector3 AxisMask(TransformGizmoAxis target) => target switch
    {
        TransformGizmoAxis.X => Vector3.UnitX,
        TransformGizmoAxis.Y => Vector3.UnitY,
        TransformGizmoAxis.Z => Vector3.UnitZ,
        TransformGizmoAxis.Xy => new Vector3(1f, 1f, 0f),
        TransformGizmoAxis.Xz => new Vector3(1f, 0f, 1f),
        TransformGizmoAxis.Yz => new Vector3(0f, 1f, 1f),
        _ => Vector3.One,
    };

    (Vector3 X, Vector3 Y, Vector3 Z) GetAxes()
    {
        var orientation = Space == TransformGizmoSpace.Local
            ? SelectedNode!.GlobalTransform.Orientation : Quaternion.Identity;
        return (Vector3.Transform(Mathf.Right, orientation), Vector3.Transform(Mathf.Up, orientation),
            Vector3.Transform(Mathf.Forward, orientation));
    }

    static float ComputeGizmoScale(ICamera camera, Vector3 anchor, Vector2 viewportSize)
    {
        var projection = camera.GetProjectionMatrix();
        var depth = projection.M44 == 1f ? 1f : MathF.Abs(Vector3.Dot(anchor - camera.Position, camera.Front));
        return MathF.Max(2f * depth * handleLengthPixels / (MathF.Abs(projection.M22) * MathF.Max(viewportSize.Y, 1f)),
            0.001f);
    }

    static Vector2 WorldToPixel(Vector3 world, ICamera camera, Vector2 viewportSize) =>
        (camera.Project(world) + Vector2.One) * 0.5f * viewportSize;

    static Vector3? IntersectScreenPlane(
        Vector2 screenPos, ICamera camera, Vector2 viewportSize, Vector3 planeOrigin)
    {
        if (CameraMath.ScreenPointToRay(camera, screenPos, viewportSize) is not { } ray) return null;
        var denominator = Vector3.Dot(camera.Front, ray.Direction);
        if (MathF.Abs(denominator) < 1e-6f) return null;
        var distance = Vector3.Dot(planeOrigin - ray.Origin, camera.Front) / denominator;
        return distance > 0f ? ray.GetPoint(distance) : null;
    }
}
