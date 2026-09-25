namespace Turian.Editor.Core;

/// <summary>
/// Interactive transform gizmo for the Scene View.
/// Drawn by <see cref="Draw"/> and driven by pointer events routed from <c>SceneViewerControl</c>.
///
/// <para>Translation and scale support axis/uniform dragging with optional snapping. Rotation is
/// visual only (arcs) — drag is not yet implemented.</para>
/// </summary>
public sealed partial class TransformGizmo
{
    const float handleThickness = 2.5f;
    const float planeOutlineThickness = 1.5f;
    const float planeFillAlpha = 0.15f;
    const float hitThresholdPx = 14f;
    const float centerHitRadiusPx = 12f;
    const float defaultGizmoScale = 1f;
    const float gizmoScaleFactor = 0.15f;
    const float uniformScaleSpeed = 0.005f;
    const int arcSegments = 48;

    // Default colors (sRGB).
    static readonly Vector4 xColor = new(1f, 0.2f, 0.2f, 1f);
    static readonly Vector4 yColor = new(0.2f, 1f, 0.2f, 1f);
    static readonly Vector4 zColor = new(0.2f, 0.2f, 1f, 1f);
    static readonly Vector4 hoverColor = new(1f, 1f, 0.2f, 1f);
    static readonly Vector4 centerColor = new(0.7f, 0.7f, 0.7f, 1f);
    static readonly Vector4 dragColor = new(1f, 1f, 1f, 1f);

    TransformGizmoAxis axis;
    bool isDragging;
    Vector2 dragStartScreen;
    Vector3 axisStartAnchorWorld;
    Vector3 axisStartNodePosition;
    Vector3 axisStartNodeScale;

    /// <summary>Gets or sets the current transform mode.</summary>
    public TransformGizmoMode Mode { get; set; } = TransformGizmoMode.Translate;

    /// <summary>Gets or sets whether the gizmo uses world or local axes.</summary>
    public TransformGizmoSpace Space { get; set; } = TransformGizmoSpace.World;

    /// <summary>Gets or sets the translation snap interval. Zero disables snapping.</summary>
    public float SnapTranslation { get; set; } = 1f;

    /// <summary>Gets or sets the rotation snap angle in degrees. Zero disables snapping.</summary>
    public float SnapRotation { get; set; } = 15f;

    /// <summary>Gets or sets the scale snap interval. Zero disables snapping.</summary>
    public float SnapScale { get; set; } = 0.1f;

    /// <summary>Gets or sets the node currently targeted by the gizmo. Null hides the gizmo.</summary>
    public Node? SelectedNode { get; set; }

    /// <summary>Gets the axis currently highlighted or being dragged.</summary>
    public TransformGizmoAxis Axis => axis;

    /// <summary>Gets a value indicating whether the gizmo is being dragged.</summary>
    public bool IsDragging => isDragging;

    /// <summary>Raised once when a drag operation begins.</summary>
    public event Action? DragStarted;

    /// <summary>Raised once when a drag operation ends.</summary>
    public event Action? DragEnded;

    /// <summary>Raised after each drag mutation applies to the selected node's transform.</summary>
    public event Action? TransformEdited;

    /// <summary>Begins a drag. Call this from the Scene View on pointer-down.</summary>
    /// <param name="screenPos">Mouse position in viewport pixels (origin top-left).</param>
    /// <param name="camera">The viewport camera.</param>
    /// <param name="viewportSize">Viewport size in pixels.</param>
    public void ProcessPointerDown(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (SelectedNode is null) return;

        var hit = HitTest(screenPos, camera, viewportSize);
        if (hit == TransformGizmoAxis.None) return;

        axis = hit;
        isDragging = true;
        dragStartScreen = screenPos;
        axisStartAnchorWorld = SelectedNode.GlobalTransform.Position;
        axisStartNodePosition = SelectedNode.Transform.Position;
        axisStartNodeScale = SelectedNode.Transform.Scale;
        DragStarted?.Invoke();
    }

    /// <summary>Updates the drag. Call from pointer-move. Updates hover highlight when not dragging.</summary>
    public void ProcessPointerMove(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (SelectedNode is null) return;

        if (!isDragging)
        {
            axis = HitTest(screenPos, camera, viewportSize);
            return;
        }

        var hitWorld = IntersectScreenPlane(screenPos, camera, viewportSize, axisStartAnchorWorld);
        if (hitWorld is null) return;

        switch (Mode)
        {
            case TransformGizmoMode.Translate:
                ApplyTranslation(hitWorld.Value);
                break;
            case TransformGizmoMode.Scale:
                ApplyScale(screenPos);
                break;
            case TransformGizmoMode.Rotate:
                break; // drag not yet implemented
        }
    }

    /// <summary>Ends the current drag. Call from pointer-up.</summary>
    public void ProcessPointerUp()
    {
        if (!isDragging) return;
        isDragging = false;
        axis = TransformGizmoAxis.None;
        DragEnded?.Invoke();
    }

    /// <summary>Draws the current gizmo state into <paramref name="gizmos"/>.</summary>
    public void Draw(Gizmos gizmos, ICamera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        ArgumentNullException.ThrowIfNull(camera);
        if (SelectedNode is null) return;

        var anchor = SelectedNode.GlobalTransform.Position;
        var (localX, localY, localZ) = GetAxes();
        var gizmoScale = ComputeGizmoScale(camera, anchor);
        if (gizmoScale < 0.001f) gizmoScale = defaultGizmoScale;

        switch (Mode)
        {
            case TransformGizmoMode.Translate:
            case TransformGizmoMode.Scale:
                DrawLinearHandles(gizmos, anchor, localX, localY, localZ, gizmoScale, camera.Front);
                break;
            case TransformGizmoMode.Rotate:
                DrawRotationArcs(gizmos, anchor, localX, localY, localZ, gizmoScale);
                break;
        }
    }

    // ── Linear handles (translate / scale) ────────────────────────────────────────────────────

    void DrawLinearHandles(
        Gizmos gizmos,
        Vector3 anchor,
        Vector3 localX,
        Vector3 localY,
        Vector3 localZ,
        float scale,
        Vector3 cameraFront)
    {
        DrawAxisArrow(gizmos, anchor, localX, scale, xColor, TransformGizmoAxis.X);
        DrawAxisArrow(gizmos, anchor, localY, scale, yColor, TransformGizmoAxis.Y);
        DrawAxisArrow(gizmos, anchor, localZ, scale, zColor, TransformGizmoAxis.Z);

        var planeSize = scale * 0.4f;
        DrawPlaneHandle(gizmos, anchor, localX, localY, zColor with { W = planeFillAlpha },
            TransformGizmoAxis.Xy, planeSize);
        DrawPlaneHandle(gizmos, anchor, localX, localZ, yColor with { W = planeFillAlpha },
            TransformGizmoAxis.Xz, planeSize);
        DrawPlaneHandle(gizmos, anchor, localY, localZ, xColor with { W = planeFillAlpha },
            TransformGizmoAxis.Yz, planeSize);

        if (Mode == TransformGizmoMode.Scale)
        {
            var radius = scale * 0.2f;
            var color = axis == TransformGizmoAxis.Center ? (isDragging ? dragColor : hoverColor) : centerColor;
            gizmos.Color = color;
            gizmos.Thickness = 2f;
            gizmos.DrawCircle(anchor, cameraFront, radius);
        }
    }

    void DrawAxisArrow(
        Gizmos gizmos,
        Vector3 anchor,
        Vector3 dir,
        float scale,
        Vector4 baseColor,
        TransformGizmoAxis thisAxis)
    {
        var color = axis == thisAxis ? (isDragging ? dragColor : hoverColor) : baseColor;
        gizmos.Color = color;
        gizmos.Thickness = handleThickness;
        var tip = anchor + dir * scale;
        gizmos.DrawLine(anchor, tip);

        // Small triangular head for visual affordance.
        var (u, v) = OrthogonalBasis(dir);
        var triSize = scale * 0.08f;
        var head = tip - dir * triSize;
        gizmos.DrawLine(head, tip + u * triSize * 0.5f);
        gizmos.DrawLine(head, tip - u * triSize * 0.5f);
        gizmos.DrawLine(head, tip + v * triSize * 0.5f);
        gizmos.DrawLine(head, tip - v * triSize * 0.5f);
    }

    void DrawPlaneHandle(
        Gizmos gizmos,
        Vector3 anchor,
        Vector3 dirA,
        Vector3 dirB,
        Vector4 fillColor,
        TransformGizmoAxis thisAxis,
        float size)
    {
        var color = axis == thisAxis ? (isDragging ? dragColor : hoverColor) : fillColor;
        gizmos.Color = color;
        gizmos.Thickness = planeOutlineThickness;
        var a = anchor + dirA * size;
        var b = anchor + dirB * size;
        var c = anchor + dirA * size + dirB * size;
        gizmos.DrawLine(anchor, a);
        gizmos.DrawLine(anchor, b);
        gizmos.DrawLine(a, c);
        gizmos.DrawLine(b, c);
    }

    // ── Rotation arcs ─────────────────────────────────────────────────────────────────────────

    void DrawRotationArcs(
        Gizmos gizmos,
        Vector3 anchor,
        Vector3 localX,
        Vector3 localY,
        Vector3 localZ,
        float scale)
    {
        var radius = scale * 0.85f;
        gizmos.Thickness = 2f;
        gizmos.Color = axis == TransformGizmoAxis.X ? hoverColor : xColor;
        DrawCircleArc(gizmos, anchor, localX, radius, localY);
        gizmos.Color = axis == TransformGizmoAxis.Y ? hoverColor : yColor;
        DrawCircleArc(gizmos, anchor, localY, radius, localZ);
        gizmos.Color = axis == TransformGizmoAxis.Z ? hoverColor : zColor;
        DrawCircleArc(gizmos, anchor, localZ, radius, localX);
    }

    static void DrawCircleArc(
        Gizmos gizmos,
        Vector3 center,
        Vector3 normal,
        float radius,
        Vector3 fromDirection)
    {
        var (u, v) = OrthogonalBasis(normal);
        var startAngle = MathF.Atan2(
            Vector3.Dot(Vector3.Normalize(fromDirection), v),
            Vector3.Dot(Vector3.Normalize(fromDirection), u));
        var steps = arcSegments;
        var prev = center + ((u * MathF.Cos(startAngle) + v * MathF.Sin(startAngle)) * radius);
        for (var i = 1; i <= steps; i++)
        {
            var a = startAngle + ((MathF.PI * 2f * i) / steps);
            var cur = center + ((u * MathF.Cos(a) + v * MathF.Sin(a)) * radius);
            gizmos.DrawLine(prev, cur);
            prev = cur;
        }
    }

    // ── Hit testing ───────────────────────────────────────────────────────────────────────────

    TransformGizmoAxis HitTest(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        var anchor = SelectedNode!.GlobalTransform.Position;
        var gizmoScale = ComputeGizmoScale(camera, anchor);
        var (localX, localY, localZ) = GetAxes();
        var anchorPx = WorldToPixel(anchor, camera, viewportSize);

        if (Mode is TransformGizmoMode.Translate or TransformGizmoMode.Scale)
        {
            // Axis handles.
            var xEnd = WorldToPixel(anchor + localX * gizmoScale, camera, viewportSize);
            var yEnd = WorldToPixel(anchor + localY * gizmoScale, camera, viewportSize);
            var zEnd = WorldToPixel(anchor + localZ * gizmoScale, camera, viewportSize);

            var best = TransformGizmoAxis.None;
            var bestDist = hitThresholdPx;
            var dX = DistToSegment(screenPos, anchorPx, xEnd);
            var dY = DistToSegment(screenPos, anchorPx, yEnd);
            var dZ = DistToSegment(screenPos, anchorPx, zEnd);
            if (dX < bestDist) { best = TransformGizmoAxis.X; bestDist = dX; }
            if (dY < bestDist) { best = TransformGizmoAxis.Y; bestDist = dY; }
            if (dZ < bestDist) best = TransformGizmoAxis.Z;
            if (best != TransformGizmoAxis.None) return best;

            // Plane handles (square defined by anchor, a, b, c = a + b).
            var planeSize = gizmoScale * 0.4f;
            var xaPx = WorldToPixel(anchor + localX * planeSize, camera, viewportSize);
            var ybPx = WorldToPixel(anchor + localY * planeSize, camera, viewportSize);
            var xyPx = WorldToPixel(anchor + localX * planeSize + localY * planeSize, camera, viewportSize);
            if (PointInQuad(screenPos, anchorPx, xaPx, ybPx, xyPx)) return TransformGizmoAxis.Xy;

            var xzPx = WorldToPixel(anchor + localX * planeSize + localZ * planeSize, camera, viewportSize);
            var zPx = WorldToPixel(anchor + localZ * planeSize, camera, viewportSize);
            if (PointInQuad(screenPos, anchorPx, xaPx, zPx, xzPx)) return TransformGizmoAxis.Xz;

            var yzPx = WorldToPixel(anchor + localY * planeSize + localZ * planeSize, camera, viewportSize);
            if (PointInQuad(screenPos, anchorPx, ybPx, zPx, yzPx)) return TransformGizmoAxis.Yz;

            // Center handle (uniform scale).
            if (Mode == TransformGizmoMode.Scale &&
                Vector2.Distance(screenPos, anchorPx) < centerHitRadiusPx)
            {
                return TransformGizmoAxis.Center;
            }
        }

        if (Mode == TransformGizmoMode.Rotate)
        {
            var radius = gizmoScale * 0.85f;
            if (HitCircle(screenPos, camera, viewportSize, anchor, localX, radius, TransformGizmoAxis.X))
                return TransformGizmoAxis.X;
            if (HitCircle(screenPos, camera, viewportSize, anchor, localY, radius, TransformGizmoAxis.Y))
                return TransformGizmoAxis.Y;
            if (HitCircle(screenPos, camera, viewportSize, anchor, localZ, radius, TransformGizmoAxis.Z))
                return TransformGizmoAxis.Z;
        }

        return TransformGizmoAxis.None;
    }

    static bool HitCircle(
        Vector2 screenPos,
        ICamera camera,
        Vector2 viewportSize,
        Vector3 anchor,
        Vector3 normal,
        float radius,
        TransformGizmoAxis axis)
    {
        _ = axis;
        var (u, v) = OrthogonalBasis(normal);
        var prev = WorldToPixel(anchor + u * radius, camera, viewportSize);
        for (var i = 1; i <= arcSegments; i++)
        {
            var a = (MathF.PI * 2f * i) / arcSegments;
            var cur = WorldToPixel(anchor + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius, camera, viewportSize);
            if (DistToSegment(screenPos, prev, cur) < hitThresholdPx) return true;
            prev = cur;
        }
        return false;
    }

    // ── Drag logic ────────────────────────────────────────────────────────────────────────────

    void ApplyTranslation(Vector3 hitWorld)
    {
        if (SelectedNode is null) return;
        var (localX, localY, localZ) = GetAxes();
        var axisDir = axis switch
        {
            TransformGizmoAxis.X => localX,
            TransformGizmoAxis.Y => localY,
            TransformGizmoAxis.Z => localZ,
            _ => Vector3.Zero,
        };
        if (Vector3.Dot(axisDir, axisDir) < 1e-9f) return;

        var delta = hitWorld - axisStartAnchorWorld;
        var t = Vector3.Dot(delta, axisDir);
        var newPos = axisStartNodePosition + (axisDir * t);
        if (SnapTranslation > 0f)
        {
            newPos = new Vector3(
                SnapValue(newPos.X, SnapTranslation),
                SnapValue(newPos.Y, SnapTranslation),
                SnapValue(newPos.Z, SnapTranslation));
        }

        SelectedNode.Transform.Position = newPos;
        TransformEdited?.Invoke();
    }

    void ApplyScale(Vector2 screenPos)
    {
        if (SelectedNode is null) return;

        var delta = screenPos.Y - dragStartScreen.Y;
        var factor = 1f + (delta * uniformScaleSpeed);
        if (SnapScale > 0f) factor = SnapValue(factor, SnapScale);
        if (factor < 0.01f) factor = 0.01f;

        if (axis == TransformGizmoAxis.Center)
        {
            SelectedNode.Transform.Scale = axisStartNodeScale * factor;
            return;
        }

        var (localX, localY, localZ) = GetAxes();
        var axisDir = axis switch
        {
            TransformGizmoAxis.X => localX,
            TransformGizmoAxis.Y => localY,
            TransformGizmoAxis.Z => localZ,
            _ => Vector3.Zero,
        };
        if (Vector3.Dot(axisDir, axisDir) < 1e-9f) return;

        var newScale = axisStartNodeScale;
        var scaleAxis = axis == TransformGizmoAxis.X ? 0 : axis == TransformGizmoAxis.Y ? 1 : 2;
        newScale = new Vector3(
            scaleAxis == 0 ? newScale.X * factor : newScale.X,
            scaleAxis == 1 ? newScale.Y * factor : newScale.Y,
            scaleAxis == 2 ? newScale.Z * factor : newScale.Z);
        SelectedNode.Transform.Scale = newScale;
        TransformEdited?.Invoke();
    }

    // ── Utilities ─────────────────────────────────────────────────────────────────────────────

    (Vector3 localX, Vector3 localY, Vector3 localZ) GetAxes()
    {
        if (Space == TransformGizmoSpace.Local && SelectedNode is not null)
        {
            var q = SelectedNode.Transform.Orientation;
            return (
                Vector3.Transform(Vector3.UnitX, q),
                Vector3.Transform(Vector3.UnitY, q),
                Vector3.Transform(Vector3.UnitZ, q));
        }
        return (Mathf.Right, Mathf.Up, Mathf.Forward);
    }

    static float ComputeGizmoScale(ICamera camera, Vector3 anchor)
    {
        var distance = Vector3.Distance(camera.Position, anchor);
        return Math.Max(distance * gizmoScaleFactor, 0.001f);
    }

    static Vector2 WorldToPixel(Vector3 world, ICamera camera, Vector2 viewportSize)
    {
        var v = new Vector4(world.X, world.Y, world.Z, 1f);
        v = Vector4.Transform(v, camera.GetViewMatrix());
        v = Vector4.Transform(v, camera.GetProjectionMatrix());
        if (MathF.Abs(v.W) > float.Epsilon) v /= v.W;
        return new Vector2((v.X + 1f) * 0.5f * viewportSize.X, (v.Y + 1f) * 0.5f * viewportSize.Y);
    }

    static Vector3? IntersectScreenPlane(
        Vector2 screenPos,
        ICamera camera,
        Vector2 viewportSize,
        Vector3 planeOrigin)
    {
        if (CameraMath.ScreenPointToRay(camera, screenPos, viewportSize) is not { } ray) return null;

        var denom = Vector3.Dot(camera.Front, ray.Direction);
        if (MathF.Abs(denom) < 1e-6f) return null;
        var t = Vector3.Dot(planeOrigin - ray.Origin, camera.Front) / denom;
        return t > 0f ? ray.GetPoint(t) : null;
    }

}
