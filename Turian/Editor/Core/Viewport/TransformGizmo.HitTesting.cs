namespace Turian.Editor.Core;

public sealed partial class TransformGizmo
{
    readonly record struct HandleHit(TransformGizmoAxis Axis, TransformGizmoMode Mode, float Distance);
    static readonly HandleHit Miss = new(TransformGizmoAxis.None, TransformGizmoMode.Translate, hitThresholdPx);

    HandleHit HitTest(Vector2 screenPos, ICamera camera, Vector2 viewportSize)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f) return Miss;
        var anchor = PivotPosition;
        var scale = ComputeGizmoScale(camera, anchor, viewportSize);
        var best = Miss;
        if (Mode != TransformGizmoMode.Rotate)
            best = HitLinearHandles(screenPos, camera, viewportSize, anchor, scale);
        if (Mode is TransformGizmoMode.Rotate or TransformGizmoMode.Combined)
        {
            var ring = HitRotationRing(screenPos, camera, viewportSize, anchor, scale * rotationRadius);
            if (ring.Distance < best.Distance) best = ring;
        }
        return best;
    }

    HandleHit HitLinearHandles(Vector2 screenPos, ICamera camera, Vector2 viewportSize, Vector3 anchor, float scale)
    {
        var centerMode = Mode == TransformGizmoMode.Translate ? TransformGizmoMode.Translate : TransformGizmoMode.Scale;
        var centerDistance = Vector2.Distance(screenPos, WorldToPixel(anchor, camera, viewportSize));
        if (centerDistance < centerHitRadiusPx) return new HandleHit(TransformGizmoAxis.Center, centerMode, 0f);
        var best = Miss;
        if (Displays(TransformGizmoMode.Translate))
            best = HitLinearTool(screenPos, camera, viewportSize, anchor, scale, TransformGizmoMode.Translate);
        if (Displays(TransformGizmoMode.Scale))
        {
            var hit = HitLinearTool(screenPos, camera, viewportSize, anchor, scale, TransformGizmoMode.Scale);
            if (hit.Distance < best.Distance) best = hit;
        }
        return best;
    }

    HandleHit HitLinearTool(Vector2 screenPos, ICamera camera, Vector2 viewportSize, Vector3 anchor, float scale,
        TransformGizmoMode operation)
    {
        var plane = HitPlaneHandles(screenPos, camera, viewportSize, anchor, scale, operation);
        if (plane.Axis != TransformGizmoAxis.None) return plane;
        var (x, y, z) = GetAxes();
        var best = Miss;
        Consider(TransformGizmoAxis.X, x);
        Consider(TransformGizmoAxis.Y, y);
        Consider(TransformGizmoAxis.Z, z);
        return best;

        void Consider(TransformGizmoAxis candidate, Vector3 direction)
        {
            var length = Mode == TransformGizmoMode.Combined && operation == TransformGizmoMode.Scale
                ? combinedScaleLength : 1f;
            var startLength = Mode == TransformGizmoMode.Combined && operation == TransformGizmoMode.Scale
                ? 1.25f : 0.13f;
            var start = WorldToPixel(anchor + direction * scale * startLength, camera, viewportSize);
            var end = WorldToPixel(anchor + direction * scale * length, camera, viewportSize);
            if (Vector2.DistanceSquared(start, end) < 16f) return;
            var distance = DistToSegment(screenPos, start, end);
            if (distance < best.Distance) best = new HandleHit(candidate, operation, distance);
        }
    }

    HandleHit HitPlaneHandles(Vector2 screenPos, ICamera camera, Vector2 viewportSize, Vector3 anchor, float scale,
        TransformGizmoMode operation)
    {
        var (x, y, z) = GetAxes();
        if (Contains(x, y)) return new HandleHit(TransformGizmoAxis.Xy, operation, 0f);
        if (Contains(x, z)) return new HandleHit(TransformGizmoAxis.Xz, operation, 0f);
        if (Contains(y, z)) return new HandleHit(TransformGizmoAxis.Yz, operation, 0f);
        return Miss;

        bool Contains(Vector3 a, Vector3 b)
        {
            var (p, q, r, s) = PlaneCorners(anchor, a, b, scale, operation);
            return PointInQuad(screenPos, WorldToPixel(p, camera, viewportSize), WorldToPixel(q, camera, viewportSize),
                WorldToPixel(s, camera, viewportSize), WorldToPixel(r, camera, viewportSize));
        }
    }

    HandleHit HitRotationRing(Vector2 screenPos, ICamera camera, Vector2 viewportSize, Vector3 anchor, float radius)
    {
        var (x, y, z) = GetAxes();
        var best = Miss;
        Consider(TransformGizmoAxis.X, x);
        Consider(TransformGizmoAxis.Y, y);
        Consider(TransformGizmoAxis.Z, z);
        return best;

        void Consider(TransformGizmoAxis candidate, Vector3 normal)
        {
            var distance = DistanceToRing(screenPos, camera, viewportSize, anchor, normal, radius);
            if (distance < best.Distance) best = new HandleHit(candidate, TransformGizmoMode.Rotate, distance);
        }
    }

    static float DistanceToRing(Vector2 screenPos, ICamera camera, Vector2 viewportSize, Vector3 anchor,
        Vector3 normal, float radius)
    {
        var (u, v) = RingBasis(normal, anchor, camera);
        var halfAngle = RingHalfAngle(normal, anchor, camera);
        var distance = float.MaxValue;
        var previous = WorldToPixel(anchor + (u * MathF.Cos(-halfAngle) + v * MathF.Sin(-halfAngle)) * radius,
            camera, viewportSize);
        for (var i = 1; i <= arcSegments; i++)
        {
            var angle = -halfAngle + halfAngle * 2f * i / arcSegments;
            var current = WorldToPixel(anchor + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius,
                camera, viewportSize);
            distance = MathF.Min(distance, DistToSegment(screenPos, previous, current));
            previous = current;
        }
        return distance;
    }
}
