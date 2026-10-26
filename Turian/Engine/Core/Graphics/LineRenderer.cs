namespace Turian.Engine.Core;

/// <summary>A textured polyline ribbon with width and RGBA samples along its length.</summary>
[ComponentContextMenu("Rendering/Line Renderer")]
[TypeId("9d19beca-4ea3-4b23-b70a-77bc9fa631d6")]
public class LineRenderer : Component
{
    /// <summary>Gets or replaces the polyline points; assigning copies the input.</summary>
    public IReadOnlyList<Vector3> Positions
    {
        get;
        set => field = Array.AsReadOnly(value.ToArray());
    } = Array.Empty<Vector3>();

    /// <summary>Whether positions are world coordinates; otherwise the node's full transform places the ribbon.</summary>
    public bool UseWorldSpace { get; set; } = true;

    /// <summary>Whether the ribbon faces each rendering camera rather than its authored normal.</summary>
    public bool Billboard { get; set; } = true;

    /// <summary>The facing direction in the positions' coordinate space when billboarding is disabled.</summary>
    public Vector3 Normal { get; set; } = Vector3.UnitZ;

    /// <summary>The nonnegative width sampled by normalized distance along the line.</summary>
    public FloatCurve WidthCurve { get; set; } = new();

    /// <summary>The linear RGBA color sampled by normalized distance along the line.</summary>
    public ColorGradient ColorGradient { get; set; } = new();

    /// <summary>Whether texture U stretches over the ribbon or repeats per coordinate-space length unit.</summary>
    public LineTextureMode TextureMode { get; set; }

    /// <summary>The optional image multiplied into the gradient; an empty reference draws solid color.</summary>
    public AssetReference<TextureAsset>? Texture { get; set; }

    /// <summary>Builds world-coordinate vertices for the supplied camera, without mutating the node.</summary>
    public virtual WorldGraphicGeometry BuildGeometry(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var positions = Positions.ToArray();
        var times = RibbonSampling.Distances(positions);
        (positions, times) = RibbonSampling.Subdivide(positions, times, WidthCurve, ColorGradient);
        return BuildRibbon(positions, times, camera);
    }

    /// <summary>Builds a ribbon with a normalized appearance coordinate for each source point.</summary>
    protected WorldGraphicGeometry BuildRibbon(Vector3[] positions, float[] times, ICamera camera)
    {
        var transform = UseWorldSpace ? Matrix4x4.Identity : Node?.GlobalTransform.Matrix4X4() ?? Matrix4x4.Identity;
        var normal = Normal;
        if (Billboard)
        {
            if (!Matrix4x4.Invert(transform, out _))
            {
                return WorldGraphicGeometry.Empty;
            }
            normal = Vector3.TransformNormal(-camera.Front, Matrix4x4.Transpose(transform));
        }
        var widths = times.Select(WidthCurve.Evaluate).ToArray();
        var colors = times.Select(ColorGradient.Evaluate).ToArray();
        var mesh = LineRendererGeometry.Build(positions, normal, widths,
            colors.Select(static color => color.Rgb).ToArray(), TextureMode);
        return ConvertVertices(mesh, positions, colors, transform);
    }

    static WorldGraphicGeometry ConvertVertices(ModelBuilder mesh, Vector3[] positions, Color[] colors,
        Matrix4x4 transform)
    {
        var vertices = new WorldGraphicVertex[mesh.Vertices.Length];
        var source = 0;
        var previous = 0;
        for (var i = 0; i < vertices.Length; i += 2)
        {
            while (source > 0 && Vector3.DistanceSquared(positions[previous], positions[source]) <= 1e-12f)
            {
                source++;
            }

            for (var side = 0; side < 2; side++)
            {
                var vertex = mesh.Vertices[i + side];
                vertices[i + side] = new(Vector3.Transform(vertex.Position, transform), (Vector4)colors[source], vertex.Uv);
            }
            previous = source++;
        }
        var indices = new uint[mesh.Indices.Length / 2];
        for (var segment = 0; segment < indices.Length / 6; segment++)
        {
            mesh.Indices.AsSpan(segment * 12, 6).CopyTo(indices.AsSpan(segment * 6));
        }
        return new(vertices, indices);
    }
}
