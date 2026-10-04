namespace Turian.Engine.Core;

public sealed partial class Gizmos
{
    /// <summary>The maximum number of filled triangles recorded per frame.</summary>
    public const int MaxTriangles = 16_384;

    const int solidSegments = 16;
    static readonly Vector3 SolidLight = Vector3.Normalize(new Vector3(-0.4f, 0.8f, -0.6f));
    readonly List<GizmoTriangle> worldTriangles = [];
    readonly List<GizmoTriangle> overlayTriangles = [];

    /// <summary>Gets filled triangles drawn against scene depth.</summary>
    public IReadOnlyList<GizmoTriangle> WorldTriangles => worldTriangles;

    /// <summary>Gets filled triangles drawn in front of scene geometry.</summary>
    public IReadOnlyList<GizmoTriangle> OverlayTriangles => overlayTriangles;

    /// <summary>Gets the number of filled triangles recorded this frame.</summary>
    public int TriangleCount => worldTriangles.Count + overlayTriangles.Count;

    /// <summary>Draws a filled triangle using the current matrix, color and depth setting.</summary>
    public void DrawTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        if (TriangleCount >= MaxTriangles)
        {
            IsOverflow = true;
            return;
        }

        var triangle = new GizmoTriangle(Vector3.Transform(a, Matrix), Vector3.Transform(b, Matrix),
            Vector3.Transform(c, Matrix), Color);
        (DepthTest ? worldTriangles : overlayTriangles).Add(triangle);
    }

    /// <summary>Draws a filled quad with vertices ordered around its perimeter.</summary>
    public void DrawQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        DrawTriangle(a, b, c);
        DrawTriangle(a, c, d);
    }

    /// <summary>Draws a shaded solid box aligned to the supplied orientation.</summary>
    public void DrawCube(Vector3 center, Vector3 size, Quaternion orientation)
    {
        var x = Vector3.Transform(Vector3.UnitX * size.X * 0.5f, orientation);
        var y = Vector3.Transform(Vector3.UnitY * size.Y * 0.5f, orientation);
        var z = Vector3.Transform(Vector3.UnitZ * size.Z * 0.5f, orientation);
        Face(center - x, -y, -z, -x);
        Face(center + x, y, z, x);
        Face(center - y, -z, -x, -y);
        Face(center + y, z, x, y);
        Face(center - z, -x, -y, -z);
        Face(center + z, x, y, z);
    }

    /// <summary>Draws a shaded cylinder between two points with closed end caps.</summary>
    public void DrawCylinder(Vector3 from, Vector3 to, float radius) => DrawRoundSolid(from, to, radius, false);

    /// <summary>Draws a shaded cone pointing from its circular base toward its tip.</summary>
    public void DrawCone(Vector3 baseCenter, Vector3 tip, float radius) =>
        DrawRoundSolid(baseCenter, tip, radius, true);

    void DrawRoundSolid(Vector3 from, Vector3 to, float radius, bool cone)
    {
        var direction = to - from;
        if (direction.LengthSquared() < 1e-9f || radius <= 0f) return;
        var normal = Vector3.Normalize(direction);
        var (u, v) = OrthonormalBasis(normal);
        for (var i = 0; i < solidSegments; i++)
        {
            var a = MathF.Tau * i / solidSegments;
            var b = MathF.Tau * (i + 1) / solidSegments;
            var offsetA = (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            var offsetB = (u * MathF.Cos(b) + v * MathF.Sin(b)) * radius;
            var color = Color;
            Shade(-normal);
            DrawTriangle(from, from + offsetB, from + offsetA);
            Color = color;
            if (cone) ConeSide(from, to, offsetA, offsetB);
            else CylinderSide(from, to, offsetA, offsetB, normal);
        }
    }

    void ConeSide(Vector3 from, Vector3 tip, Vector3 a, Vector3 b)
    {
        var color = Color;
        Shade(Vector3.Cross(b - a, tip - from - a));
        DrawTriangle(from + a, from + b, tip);
        Color = color;
    }

    void CylinderSide(Vector3 from, Vector3 to, Vector3 a, Vector3 b, Vector3 normal)
    {
        var color = Color;
        Shade(normal);
        DrawTriangle(to, to + a, to + b);
        Color = color;
        Shade(a + b);
        DrawQuad(from + a, from + b, to + b, to + a);
        Color = color;
    }

    void Face(Vector3 center, Vector3 a, Vector3 b, Vector3 normal)
    {
        var color = Color;
        Shade(normal);
        DrawQuad(center - a - b, center + a - b, center + a + b, center - a + b);
        Color = color;
    }

    void Shade(Vector3 normal)
    {
        normal = Vector3.Normalize(Vector3.TransformNormal(normal, Matrix));
        var brightness = 0.72f + 0.28f * MathF.Max(0f, Vector3.Dot(normal, SolidLight));
        Color = new Vector4(Color.X * brightness, Color.Y * brightness, Color.Z * brightness, Color.W);
    }
}
