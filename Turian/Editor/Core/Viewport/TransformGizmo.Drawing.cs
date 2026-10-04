namespace Turian.Editor.Core;

public sealed partial class TransformGizmo
{
    /// <summary>Draws solid handles and rotation feedback in front of the scene, preserving drawing state.</summary>
    public void Draw(Gizmos gizmos, ICamera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        ArgumentNullException.ThrowIfNull(camera);
        if (!CanInteract) return;
        var state = (gizmos.DepthTest, gizmos.Matrix, gizmos.Color, gizmos.Thickness);
        try
        {
            gizmos.DepthTest = false;
            gizmos.Matrix = Matrix4x4.Identity;
            var anchor = SelectedNode!.GlobalTransform.Position;
            var scale = ComputeGizmoScale(camera, anchor, viewportSize);
            if (Mode != TransformGizmoMode.Rotate) DrawLinearHandles(gizmos, anchor, scale);
            if (Mode is TransformGizmoMode.Rotate or TransformGizmoMode.Combined)
                DrawRotationHandles(gizmos, camera, viewportSize, anchor, scale);
        }
        finally
        {
            (gizmos.DepthTest, gizmos.Matrix, gizmos.Color, gizmos.Thickness) = state;
        }
    }

    void DrawLinearHandles(Gizmos gizmos, Vector3 anchor, float scale)
    {
        var (x, y, z) = GetAxes();
        if (Displays(TransformGizmoMode.Translate))
        {
            DrawArrow(gizmos, anchor, x, scale, XColor, TransformGizmoAxis.X);
            DrawArrow(gizmos, anchor, y, scale, YColor, TransformGizmoAxis.Y);
            DrawArrow(gizmos, anchor, z, scale, ZColor, TransformGizmoAxis.Z);
            DrawPlanes(gizmos, anchor, scale, TransformGizmoMode.Translate);
        }
        if (Displays(TransformGizmoMode.Scale))
        {
            var length = Mode == TransformGizmoMode.Combined ? combinedScaleLength : 1f;
            DrawScaleHandle(gizmos, anchor, x, scale, length, XColor, TransformGizmoAxis.X);
            DrawScaleHandle(gizmos, anchor, y, scale, length, YColor, TransformGizmoAxis.Y);
            DrawScaleHandle(gizmos, anchor, z, scale, length, ZColor, TransformGizmoAxis.Z);
            DrawPlanes(gizmos, anchor, scale, TransformGizmoMode.Scale);
        }
        var centerMode = Mode == TransformGizmoMode.Translate ? TransformGizmoMode.Translate : TransformGizmoMode.Scale;
        gizmos.Color = HandleColor(TransformGizmoAxis.Center, centerMode, CenterColor);
        gizmos.DrawCube(anchor, new Vector3(scale * 0.13f), Quaternion.Identity);
    }

    void DrawArrow(Gizmos gizmos, Vector3 anchor, Vector3 direction, float scale, Vector4 color,
        TransformGizmoAxis target)
    {
        gizmos.Color = HandleColor(target, TransformGizmoMode.Translate, color);
        var head = anchor + direction * scale * 0.78f;
        gizmos.DrawCylinder(anchor + direction * scale * 0.13f, head, scale * 0.022f);
        gizmos.DrawCone(head, anchor + direction * scale, scale * 0.075f);
    }

    void DrawScaleHandle(Gizmos gizmos, Vector3 anchor, Vector3 direction, float scale, float length,
        Vector4 color, TransformGizmoAxis target)
    {
        gizmos.Color = HandleColor(target, TransformGizmoMode.Scale, color);
        var end = anchor + direction * scale * length;
        var start = Mode == TransformGizmoMode.Combined ? 1.25f : 0.13f;
        gizmos.DrawCylinder(anchor + direction * scale * start, end, scale * 0.02f);
        var orientation = Space == TransformGizmoSpace.Local
            ? SelectedNode!.GlobalTransform.Orientation : Quaternion.Identity;
        gizmos.DrawCube(end, new Vector3(scale * 0.13f), orientation);
    }

    void DrawPlanes(Gizmos gizmos, Vector3 anchor, float scale, TransformGizmoMode operation)
    {
        var (x, y, z) = GetAxes();
        DrawPlane(gizmos, anchor, x, y, scale, ZColor, TransformGizmoAxis.Xy, operation);
        DrawPlane(gizmos, anchor, x, z, scale, YColor, TransformGizmoAxis.Xz, operation);
        DrawPlane(gizmos, anchor, y, z, scale, XColor, TransformGizmoAxis.Yz, operation);
    }

    void DrawPlane(Gizmos gizmos, Vector3 anchor, Vector3 x, Vector3 y, float scale, Vector4 color,
        TransformGizmoAxis target, TransformGizmoMode operation)
    {
        var (a, b, c, d) = PlaneCorners(anchor, x, y, scale, operation);
        var selected = axis == target && handleMode == operation;
        color = HandleColor(target, operation, color);
        gizmos.Color = color with { W = selected ? 0.7f : 0.38f };
        gizmos.DrawQuad(a, b, c, d);
        gizmos.Color = color with { W = selected ? 1f : 0.85f };
        gizmos.Thickness = selected ? 3f : 2f;
        gizmos.DrawLine(a, b);
        gizmos.DrawLine(b, c);
        gizmos.DrawLine(c, d);
        gizmos.DrawLine(d, a);
    }

    (Vector3 A, Vector3 B, Vector3 C, Vector3 D) PlaneCorners(
        Vector3 anchor, Vector3 x, Vector3 y, float scale, TransformGizmoMode operation)
    {
        var sign = Mode == TransformGizmoMode.Combined && operation == TransformGizmoMode.Scale ? -1f : 1f;
        x *= scale * sign;
        y *= scale * sign;
        return (anchor + (x + y) * 0.23f, anchor + x * 0.43f + y * 0.23f,
            anchor + (x + y) * 0.43f, anchor + x * 0.23f + y * 0.43f);
    }

    Vector4 HandleColor(TransformGizmoAxis target, TransformGizmoMode operation, Vector4 color)
    {
        if (axis != target || handleMode != operation) return color;
        return isDragging ? DragColor : HoverColor;
    }

    void DrawRotationHandles(Gizmos gizmos, ICamera camera, Vector2 viewportSize, Vector3 anchor, float scale)
    {
        var (x, y, z) = GetAxes();
        var radius = scale * rotationRadius;
        DrawRing(gizmos, camera, anchor, x, radius, XColor, TransformGizmoAxis.X);
        DrawRing(gizmos, camera, anchor, y, radius, YColor, TransformGizmoAxis.Y);
        DrawRing(gizmos, camera, anchor, z, radius, ZColor, TransformGizmoAxis.Z);
        if (axis == TransformGizmoAxis.None || handleMode != TransformGizmoMode.Rotate) return;
        gizmos.Color = isDragging ? DragColor : HoverColor;
        gizmos.Thickness = 2f;
        if (isDragging) DrawRotationSweep(gizmos, radius);
        else
        {
            var normal = axis == TransformGizmoAxis.X ? x : axis == TransformGizmoAxis.Y ? y : z;
            var direction = ClosestRingDirection(pointerScreen, camera, viewportSize, anchor, normal, radius);
            gizmos.DrawLine(anchor, anchor + direction * radius);
        }
    }

    void DrawRing(Gizmos gizmos, ICamera camera, Vector3 anchor, Vector3 normal, float radius, Vector4 color,
        TransformGizmoAxis target)
    {
        var selected = axis == target && handleMode == TransformGizmoMode.Rotate;
        if (selected && isDragging) normal = rotationAxis;
        var (u, v) = RingBasis(normal, anchor, camera);
        var halfAngle = RingHalfAngle(normal, anchor, camera);
        gizmos.Color = HandleColor(target, TransformGizmoMode.Rotate, color);
        gizmos.Thickness = selected ? 6f : handleThickness;
        var previous = anchor + (u * MathF.Cos(-halfAngle) + v * MathF.Sin(-halfAngle)) * radius;
        for (var i = 1; i <= arcSegments; i++)
        {
            var angle = -halfAngle + halfAngle * 2f * i / arcSegments;
            var current = anchor + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
            gizmos.DrawLine(previous, current);
            previous = current;
        }
    }

    void DrawRotationSweep(Gizmos gizmos, float radius)
    {
        var anchor = axisStartAnchorWorld;
        var angle = Math.Clamp(AppliedRotationAngle, -MathF.Tau, MathF.Tau);
        var previous = anchor + rotationStartDirection * radius;
        var color = gizmos.Color;
        gizmos.DrawLine(anchor, previous);
        for (var i = 1; i <= arcSegments; i++)
        {
            var direction = Vector3.Transform(rotationStartDirection,
                Quaternion.CreateFromAxisAngle(rotationAxis, angle * i / arcSegments));
            var current = anchor + direction * radius;
            gizmos.Color = color with { W = 0.18f };
            gizmos.DrawTriangle(anchor, previous, current);
            gizmos.Color = color;
            gizmos.DrawLine(previous, current);
            previous = current;
        }
        gizmos.DrawLine(anchor, previous);
    }

    static (Vector3 U, Vector3 V) RingBasis(Vector3 normal, Vector3 anchor, ICamera camera)
    {
        var towardCamera = camera.Position - anchor;
        var projected = towardCamera - normal * Vector3.Dot(towardCamera, normal);
        var u = projected.LengthSquared() < 1e-6f ? OrthogonalBasis(normal).U : Vector3.Normalize(projected);
        return (u, Vector3.Cross(normal, u));
    }

    static float RingHalfAngle(Vector3 normal, Vector3 anchor, ICamera camera)
    {
        var alignment = MathF.Abs(Vector3.Dot(normal, Vector3.Normalize(camera.Position - anchor)));
        return MathF.PI / 2f * (1f + Math.Clamp((alignment - 0.99f) / 0.005f, 0f, 1f));
    }
}
