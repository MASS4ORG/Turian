namespace Turian.Editor.Core;

/// <summary>Draws a world-aligned reference grid with intermediate and major line intervals.</summary>
public static class GroundGrid
{
    /// <summary>The default world-space size of a grid cell.</summary>
    public const float CellSize = 1f;
    /// <summary>The default number of cells either side of the camera.</summary>
    public const int HalfExtent = 50;
    /// <summary>The default interval of major lines in cells.</summary>
    public const int MajorEvery = 10;

    static readonly SceneGridSettings Defaults = new();

    /// <summary>Draws the configured grid around the camera while preserving the caller's drawing state.</summary>
    public static void Draw(Gizmos gizmos, Vector3 cameraPosition, SceneGridSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        settings ??= Defaults;
        if (!settings.Visible) return;
        var state = (gizmos.Color, gizmos.Thickness, gizmos.Matrix, gizmos.DepthTest);
        try
        {
            gizmos.Matrix = Matrix4x4.Identity;
            gizmos.DepthTest = true;
            var (u, v) = Basis(settings.Plane);
            var cell = Math.Clamp(settings.CellSize, 0.01f, 100f);
            var count = Math.Clamp(settings.HalfExtent, 5, 500);
            var originU = (int)MathF.Floor(Vector3.Dot(cameraPosition, u) / cell);
            var originV = (int)MathF.Floor(Vector3.Dot(cameraPosition, v) / cell);
            var extent = count * cell;
            for (var i = -count; i <= count; i++)
            {
                DrawLine(gizmos, u * ((originU + i) * cell) + v * (originV * cell - extent),
                    u * ((originU + i) * cell) + v * (originV * cell + extent), originU + i, v, settings);
                DrawLine(gizmos, v * ((originV + i) * cell) + u * (originU * cell - extent),
                    v * ((originV + i) * cell) + u * (originU * cell + extent), originV + i, u, settings);
            }
            if (settings.ShowNormalAxis)
            {
                var normal = Vector3.Cross(u, v);
                gizmos.Color = AxisColor(normal, settings.AxisOpacity);
                gizmos.Thickness = settings.AxisThickness;
                gizmos.DrawLine(-normal * extent, normal * extent);
            }
        }
        finally
        {
            (gizmos.Color, gizmos.Thickness, gizmos.Matrix, gizmos.DepthTest) = state;
        }
    }

    static (Vector3 U, Vector3 V) Basis(SceneGridPlane plane) => plane switch
    {
        SceneGridPlane.Xz => (Vector3.UnitX, Vector3.UnitZ),
        SceneGridPlane.Yz => (Vector3.UnitY, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitY)
    };

    static void DrawLine(Gizmos gizmos, Vector3 from, Vector3 to, int index, Vector3 direction,
        SceneGridSettings settings)
    {
        if (index == 0 && settings.ShowPlaneAxes)
        {
            gizmos.Color = AxisColor(direction, settings.AxisOpacity);
            gizmos.Thickness = settings.AxisThickness;
        }
        else if (index % Math.Max(1, settings.MajorEvery) == 0)
        {
            gizmos.Color = new Vector4(1, 1, 1, settings.MajorOpacity);
            gizmos.Thickness = settings.MajorThickness;
        }
        else if (index % Math.Max(1, settings.IntermediateEvery) == 0)
        {
            gizmos.Color = new Vector4(1, 1, 1, settings.IntermediateOpacity);
            gizmos.Thickness = settings.IntermediateThickness;
        }
        else
        {
            gizmos.Color = new Vector4(1, 1, 1, settings.MinorOpacity);
            gizmos.Thickness = settings.MinorThickness;
        }
        gizmos.DrawLine(from, to);
    }

    static Vector4 AxisColor(Vector3 axis, float opacity) =>
        new(MathF.Abs(axis.X) * 0.9f + MathF.Abs(axis.Y) * 0.3f + MathF.Abs(axis.Z) * 0.25f,
            MathF.Abs(axis.X) * 0.3f + MathF.Abs(axis.Y) * 0.85f + MathF.Abs(axis.Z) * 0.55f,
            MathF.Abs(axis.X) * 0.35f + MathF.Abs(axis.Y) * 0.5f + MathF.Abs(axis.Z) * 0.95f, opacity);
}
