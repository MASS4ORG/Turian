namespace Turian.Editor.Core;

public sealed partial class TransformGizmo
{
    Quaternion rotationStartOrientation;
    Quaternion rotationParentOrientation;
    Vector3 rotationAxis;
    Vector3 rotationPreviousDirection;
    Vector3 rotationStartDirection;
    Vector2 rotationScreenTangent;
    Vector2 rotationPreviousScreen;
    float rotationAngle;
    bool rotationUsesPlane;

    float AppliedRotationAngle => SnapEnabled && SnapRotation > 0f
        ? SnapValue(rotationAngle, SnapRotation * MathF.PI / 180f) : rotationAngle;

    void BeginRotation(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        var (x, y, z) = GetAxes();
        rotationAxis = axis switch
        {
            TransformGizmoAxis.X => x,
            TransformGizmoAxis.Y => y,
            _ => z,
        };
        rotationStartOrientation = SelectedNode!.GlobalTransform.Orientation;
        rotationParentOrientation = SelectedNode.Parent?.GlobalTransform.Orientation ?? Quaternion.Identity;
        rotationAngle = 0f;
        rotationPreviousScreen = screenPos;
        rotationUsesPlane = MathF.Abs(Vector3.Dot(rotationAxis, camera.Front)) > 0.05f;
        rotationPreviousDirection = RotationDirection(screenPos, camera, viewportSize) ?? Vector3.Zero;

        var radius = ComputeGizmoScale(camera, axisStartAnchorWorld, viewportSize) * rotationRadius;
        var direction = ClosestRingDirection(screenPos, camera, viewportSize, axisStartAnchorWorld, rotationAxis, radius);
        rotationStartDirection = rotationPreviousDirection.LengthSquared() > 0f ? rotationPreviousDirection : direction;
        var point = axisStartAnchorWorld + direction * radius;
        rotationScreenTangent = WorldToPixel(point + Vector3.Cross(rotationAxis, direction) * radius,
            camera, viewportSize) - WorldToPixel(point, camera, viewportSize);
    }

    void ApplyRotation(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        if (rotationUsesPlane)
        {
            if (RotationDirection(screenPos, camera, viewportSize) is not { } direction) return;
            if (rotationPreviousDirection.LengthSquared() > 0f)
            {
                rotationAngle += MathF.Atan2(
                    Vector3.Dot(rotationAxis, Vector3.Cross(rotationPreviousDirection, direction)),
                    Vector3.Dot(rotationPreviousDirection, direction));
            }
            rotationPreviousDirection = direction;
        }
        else
        {
            var lengthSquared = rotationScreenTangent.LengthSquared();
            if (lengthSquared < 1e-6f) return;
            rotationAngle += Vector2.Dot(screenPos - rotationPreviousScreen, rotationScreenTangent) / lengthSquared;
        }

        rotationPreviousScreen = screenPos;
        var orientation = Quaternion.CreateFromAxisAngle(rotationAxis, AppliedRotationAngle) * rotationStartOrientation;
        SelectedNode!.Orientation = Quaternion.Normalize(Quaternion.Inverse(rotationParentOrientation) * orientation);
        TransformEdited?.Invoke();
    }

    Vector3? RotationDirection(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        if (CameraMath.ScreenPointToRay(camera, screenPos, viewportSize) is not { } ray) return null;
        var denominator = Vector3.Dot(rotationAxis, ray.Direction);
        if (MathF.Abs(denominator) < 1e-6f) return null;
        var distance = Vector3.Dot(axisStartAnchorWorld - ray.Origin, rotationAxis) / denominator;
        if (distance <= 0f) return null;
        var direction = ray.GetPoint(distance) - axisStartAnchorWorld;
        return direction.LengthSquared() < 1e-9f ? null : Vector3.Normalize(direction);
    }

    static Vector3 ClosestRingDirection(Vector2 screenPos, ICamera camera, Vector2 viewportSize, Vector3 anchor,
        Vector3 normal, float radius)
    {
        var (u, v) = RingBasis(normal, anchor, camera);
        var halfAngle = RingHalfAngle(normal, anchor, camera);
        var closest = u;
        var bestDistance = float.MaxValue;
        for (var i = 0; i <= arcSegments; i++)
        {
            var angle = -halfAngle + halfAngle * 2f * i / arcSegments;
            var direction = u * MathF.Cos(angle) + v * MathF.Sin(angle);
            var pixel = WorldToPixel(anchor + direction * radius, camera, viewportSize);
            var distance = Vector2.DistanceSquared(screenPos, pixel);
            if (distance >= bestDistance) continue;
            closest = direction;
            bestDistance = distance;
        }
        return closest;
    }
}
