namespace Turian.Tests;

/// <summary>Checks ribbon dimensions, sampled attributes, joins and safe handling of degenerate input.</summary>
public sealed class LineRendererGeometryTests
{
    /// <summary>Widths, colors and UVs follow the original samples and geometric length.</summary>
    [Theory]
    [InlineData(LineTextureMode.Stretch, 1f)]
    [InlineData(LineTextureMode.Tile, 4f)]
    public void SamplesDescribeRibbonVertices(LineTextureMode mode, float finalU)
    {
        var mesh = LineRendererGeometry.Build([Vector3.Zero, new Vector3(2, 0, 0), new Vector3(4, 0, 0)],
            Vector3.UnitZ, [2, 1, 0], [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ], mode);

        Assert.Equal(6, mesh.Vertices.Length);
        Assert.Equal(24, mesh.Indices.Length);
        Assert.Equal(new Vector3(0, -1, 0), mesh.Vertices[0].Position);
        Assert.Equal(new Vector3(0, 1, 0), mesh.Vertices[1].Position);
        Assert.Equal(new Vector3(2, -0.5f, 0), mesh.Vertices[2].Position);
        Assert.Equal(new Vector3(4, 0, 0), mesh.Vertices[4].Position);
        Assert.Equal(new Vector3(4, 0, 0), mesh.Vertices[5].Position);
        Assert.Equal(Vector3.UnitX, mesh.Vertices[0].Color);
        Assert.Equal(Vector3.UnitY, mesh.Vertices[2].Color);
        Assert.Equal(Vector3.UnitZ, mesh.Vertices[4].Color);
        Assert.All(mesh.Vertices, static vertex => Assert.Equal(Vector3.UnitZ, vertex.Normal));
        Assert.Equal(new Vector2(0, 0), mesh.Vertices[0].Uv);
        Assert.Equal(new Vector2(finalU * 0.5f, 1), mesh.Vertices[3].Uv);
        Assert.Equal(new Vector2(finalU, 1), mesh.Vertices[5].Uv);
        Assert.All(mesh.Indices, static index => Assert.InRange(index, 0u, 5u));
        var a = mesh.Vertices[mesh.Indices[0]].Position;
        var b = mesh.Vertices[mesh.Indices[1]].Position;
        var c = mesh.Vertices[mesh.Indices[2]].Position;
        var backA = mesh.Vertices[mesh.Indices[6]].Position;
        var backB = mesh.Vertices[mesh.Indices[7]].Position;
        var backC = mesh.Vertices[mesh.Indices[8]].Position;
        Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), Vector3.Cross(backB - backA, backC - backA)) < 0);
    }

    /// <summary>Omitted samples use width one and white; a shared sample applies to every point.</summary>
    [Fact]
    public void DefaultAndSharedSamplesApplyToEveryPoint()
    {
        Vector3[] points = [Vector3.Zero, Vector3.UnitX];
        var defaults = LineRendererGeometry.Build(points, Vector3.UnitZ);
        Assert.Equal(1f, Vector3.Distance(defaults.Vertices[0].Position, defaults.Vertices[1].Position));
        Assert.All(defaults.Vertices, static vertex => Assert.Equal(Vector3.One, vertex.Color));
        var shared = LineRendererGeometry.Build(points, Vector3.UnitZ, [0.2f], [new Vector3(0.2f, 0.4f, 0.6f)]);
        Assert.Equal(0.2f, Vector3.Distance(shared.Vertices[2].Position, shared.Vertices[3].Position), 0.001f);
        Assert.All(shared.Vertices, static vertex => Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), vertex.Color));
    }

    /// <summary>Duplicate points are removed without shifting the surviving points' width and color samples.</summary>
    [Fact]
    public void DuplicatePointsRetainSampleIdentity()
    {
        var mesh = LineRendererGeometry.Build([Vector3.Zero, Vector3.Zero, Vector3.UnitX], Vector3.UnitZ,
            [2, 99, 4], [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]);
        Assert.Equal(4, mesh.Vertices.Length);
        Assert.Equal(2f, Vector3.Distance(mesh.Vertices[0].Position, mesh.Vertices[1].Position));
        Assert.Equal(4f, Vector3.Distance(mesh.Vertices[2].Position, mesh.Vertices[3].Position));
        Assert.Equal(Vector3.UnitZ, mesh.Vertices[2].Color);
        Assert.Empty(LineRendererGeometry.Build([], Vector3.UnitZ).Vertices);
        Assert.Empty(LineRendererGeometry.Build([Vector3.One], Vector3.UnitZ).Indices);
        Assert.Empty(LineRendererGeometry.Build([Vector3.One, Vector3.One], Vector3.UnitZ).Vertices);
    }

    /// <summary>Corners share their miter vertices and reversals or view-parallel edges remain finite.</summary>
    [Fact]
    public void JoinsRemainBoundedAndFinite()
    {
        var corner = LineRendererGeometry.Build([Vector3.Zero, Vector3.UnitX, new Vector3(1, 1, 0)],
            Vector3.UnitZ, [2]);
        Assert.True(Vector3.Distance(new Vector3(2, -1, 0), corner.Vertices[2].Position) < 0.001f);
        Assert.True(Vector3.Distance(new Vector3(0, 1, 0), corner.Vertices[3].Position) < 0.001f);
        Vector3[][] paths =
        [
            [Vector3.Zero, Vector3.UnitX, Vector3.Zero],
            [Vector3.Zero, Vector3.UnitX, new Vector3(0, 0.001f, 0)],
            [Vector3.Zero, Vector3.UnitZ],
            [Vector3.Zero, Vector3.UnitX],
        ];
        foreach (var path in paths)
        {
            var mesh = LineRendererGeometry.Build(path, path.Length == 2 ? path[1] : Vector3.UnitZ);
            Assert.All(mesh.Vertices, vertex =>
            {
                Assert.True(float.IsFinite(vertex.Position.X + vertex.Position.Y + vertex.Position.Z));
                Assert.True(Vector3.Distance(vertex.Position, path[Array.IndexOf(mesh.Vertices, vertex) / 2]) <= 2.51f);
            });
        }
    }

    /// <summary>Malformed samples, nonfinite values and unrepresentable lengths fail before producing GPU data.</summary>
    [Fact]
    public void InvalidInputsAreRejected()
    {
        Vector3[] points = [Vector3.Zero, Vector3.UnitX, Vector3.One];
        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, Vector3.UnitZ, [1, 2]));
        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, Vector3.UnitZ,
            colors: [Vector3.One, Vector3.One]));
        Assert.Throws<ArgumentOutOfRangeException>(() => LineRendererGeometry.Build(points, Vector3.UnitZ, [-1]));
        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, Vector3.UnitZ, [float.NaN]));
        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, Vector3.UnitZ, [float.PositiveInfinity]));
        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, new Vector3(float.MaxValue)));
        foreach (var invalid in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.NaN, 0),
                     new Vector3(0, 0, float.PositiveInfinity) })
        {
            Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build([Vector3.Zero, invalid], Vector3.UnitZ));
            Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, invalid));
            Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(points, Vector3.UnitZ, colors: [invalid]));
        }

        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(
            [new Vector3(-float.MaxValue, 0, 0), new Vector3(float.MaxValue, 0, 0)], Vector3.UnitZ));
        Assert.Throws<ArgumentException>(() => LineRendererGeometry.Build(
            [Vector3.Zero, Vector3.UnitX, new Vector3(0, 0.001f, 0)], Vector3.UnitZ, [float.MaxValue]));
        Assert.Throws<ArgumentOutOfRangeException>(() => LineRendererGeometry.Build(points, Vector3.UnitZ,
            textureMode: (LineTextureMode)99));
    }

}
