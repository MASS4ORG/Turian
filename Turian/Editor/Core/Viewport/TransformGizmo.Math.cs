namespace Turian.Editor.Core;

public sealed partial class TransformGizmo
{
    // Shared geometric helpers keep hit testing and drag rendering numerically consistent.
    static (Vector3 U, Vector3 V) OrthogonalBasis(Vector3 normal)
    {
        normal = Vector3.Normalize(normal);
        var fallback = MathF.Abs(normal.Y) < 0.99f ? Mathf.Up : Mathf.Right;
        var u = Vector3.Normalize(Vector3.Cross(normal, fallback));
        if (Vector3.Dot(u, u) < 1e-9f) u = new Vector3(0f, 0f, 1f);
        var v = Vector3.Cross(normal, u);
        return (u, v);
    }

    static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var ap = p - a;
        var lenSq = Vector2.Dot(ab, ab);
        if (lenSq < 1e-6f) return Vector2.Distance(p, a);
        var t = Math.Clamp(Vector2.Dot(ap, ab) / lenSq, 0f, 1f);
        var proj = a + (ab * t);
        return Vector2.Distance(p, proj);
    }

    static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var v0 = c - a;
        var v1 = b - a;
        var v2 = p - a;
        var dot00 = Vector2.Dot(v0, v0);
        var dot01 = Vector2.Dot(v0, v1);
        var dot02 = Vector2.Dot(v0, v2);
        var dot11 = Vector2.Dot(v1, v1);
        var dot12 = Vector2.Dot(v1, v2);
        var inv = 1f / ((dot00 * dot11) - (dot01 * dot01));
        var u = ((dot11 * dot02) - (dot01 * dot12)) * inv;
        var v = ((dot00 * dot12) - (dot01 * dot02)) * inv;
        return u >= 0f && v >= 0f && (u + v) <= 1f;
    }

    static float SnapValue(float value, float snap) =>
        MathF.Round(value / snap) * snap;

    static bool PointInQuad(
        Vector2 p,
        Vector2 anchor,
        Vector2 a,
        Vector2 b,
        Vector2 c) =>
        PointInTriangle(p, anchor, a, c) || PointInTriangle(p, anchor, b, c);
}
