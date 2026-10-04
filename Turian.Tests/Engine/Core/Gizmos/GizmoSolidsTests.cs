namespace Turian.Tests;

/// <summary>Checks filled gizmo primitives and the triangle expansion used by thick lines.</summary>
public sealed class GizmoSolidsTests
{
    /// <summary>Triangles follow drawing state and clearing removes both depth collections.</summary>
    [Fact]
    public void Triangles_RespectMatrixColorDepthAndClear()
    {
        var gizmos = new Gizmos
        {
            Color = new Vector4(0.5f, 0.6f, 0.7f, 0.8f),
            Matrix = Matrix4x4.CreateTranslation(2, 3, 4)
        };
        gizmos.DrawTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var triangle = Assert.Single(gizmos.WorldTriangles);
        Assert.Equal(new Vector3(2, 3, 4), triangle.A);
        Assert.Equal(new Vector3(3, 3, 4), triangle.B);
        Assert.Equal(new Vector3(2, 4, 4), triangle.C);
        Assert.Equal(gizmos.Color, triangle.Color);
        gizmos.DepthTest = false;
        gizmos.DrawQuad(Vector3.Zero, Vector3.UnitX, Vector3.One, Vector3.UnitY);
        Assert.Equal(2, gizmos.OverlayTriangles.Count);
        gizmos.Clear();
        Assert.Equal(0, gizmos.TriangleCount);
    }

    /// <summary>Solid primitives form filled surfaces without leaking their face shading color.</summary>
    [Fact]
    public void Solids_AreClosedAndPreserveColor()
    {
        var gizmos = new Gizmos { Color = new Vector4(1f, 0.4f, 0.3f, 1f) };
        var color = gizmos.Color;
        gizmos.DrawCube(Vector3.Zero, Vector3.One, Quaternion.Identity);
        Assert.Equal(12, gizmos.TriangleCount);
        Assert.Equal(color, gizmos.Color);
        gizmos.DrawCone(Vector3.Zero, Vector3.UnitX, 0.2f);
        Assert.Equal(12 + 32, gizmos.TriangleCount);
        Assert.Equal(color, gizmos.Color);
        gizmos.DrawCylinder(Vector3.Zero, Vector3.UnitY, 0.1f);
        Assert.Equal(12 + 32 + 64, gizmos.TriangleCount);
        Assert.Equal(color, gizmos.Color);
        Assert.All(gizmos.WorldTriangles, triangle =>
            Assert.True(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A).LengthSquared() > 0f));
    }

    /// <summary>Zero-length shafts and nonpositive radii emit no invalid geometry.</summary>
    [Fact]
    public void RoundSolids_IgnoreDegenerateGeometry()
    {
        var gizmos = new Gizmos();
        gizmos.DrawCylinder(Vector3.Zero, Vector3.Zero, 1f);
        gizmos.DrawCone(Vector3.Zero, Vector3.UnitX, 0f);
        Assert.Equal(0, gizmos.TriangleCount);
    }

    /// <summary>The triangle cap limits memory independently of line capacity.</summary>
    [Fact]
    public void TriangleOverflow_IsBoundedAndReset()
    {
        var gizmos = new Gizmos();
        for (var i = 0; i <= Gizmos.MaxTriangles; i++)
            gizmos.DrawTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        Assert.True(gizmos.IsOverflow);
        Assert.Equal(Gizmos.MaxTriangles, gizmos.TriangleCount);
        gizmos.Clear();
        Assert.False(gizmos.IsOverflow);
    }

    /// <summary>Each thick line covers a complete rectangle with two nondegenerate triangles.</summary>
    [Fact]
    public void ExpandedLine_CoversBothHalvesOfItsQuad()
    {
        var vertices = new GizmoExpandedVertex[6];
        var line = new GizmoLine(Vector3.Zero, Vector3.UnitX, Vector4.One, 4f);
        GizmoRenderSystem.ExpandLine(vertices, line);
        var first = Area(vertices[0], vertices[1], vertices[2]);
        var second = Area(vertices[3], vertices[4], vertices[5]);
        Assert.Equal(2f, first);
        Assert.Equal(first, second);
        Assert.Equal(4, vertices.Select(vertex => (vertex.Side, vertex.End)).Distinct().Count());
    }

    static float Area(GizmoExpandedVertex a, GizmoExpandedVertex b, GizmoExpandedVertex c) =>
        MathF.Abs((b.Side - a.Side) * (c.End - a.End) - (b.End - a.End) * (c.Side - a.Side)) * 0.5f;
}
