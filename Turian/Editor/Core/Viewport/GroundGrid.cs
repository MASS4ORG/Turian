namespace Turian.Editor.Core;

/// <summary>
/// The reference grid drawn on the world's ground plane. With free camera rotation the view can end
/// up rolled or upside down, and a scene of loose geometry gives nothing to orient against — the
/// grid is what tells you where the floor is and which way you are facing.
/// </summary>
public static class GroundGrid
{
    /// <summary>World-space size of one grid cell.</summary>
    public const float CellSize = 1f;

    /// <summary>Cells drawn either side of the origin along each axis.</summary>
    public const int HalfExtent = 50;

    /// <summary>Every Nth line is drawn brighter, so distance is readable at a glance.</summary>
    public const int MajorEvery = 10;

    static readonly Vector4 minorColor = new(1f, 1f, 1f, 0.06f);
    static readonly Vector4 majorColor = new(1f, 1f, 1f, 0.16f);
    static readonly Vector4 axisXColor = new(0.90f, 0.25f, 0.30f, 0.65f);
    static readonly Vector4 axisZColor = new(0.25f, 0.55f, 0.95f, 0.65f);

    /// <summary>
    /// Draws the grid on the y = 0 plane, centred on the camera so it always extends to the horizon
    /// rather than running out underfoot. The two world axes through the origin are colored.
    /// </summary>
    /// <param name="gizmos">The gizmo buffer to append to.</param>
    /// <param name="cameraPosition">Camera position, used to centre the grid.</param>
    public static void Draw(Gizmos gizmos, Vector3 cameraPosition)
    {
        ArgumentNullException.ThrowIfNull(gizmos);

        var previousColor = gizmos.Color;
        var previousThickness = gizmos.Thickness;
        gizmos.Thickness = 1f;

        // Snap to the cell lattice so the grid does not shimmer as the camera moves.
        var originX = MathF.Floor(cameraPosition.X / CellSize) * CellSize;
        var originZ = MathF.Floor(cameraPosition.Z / CellSize) * CellSize;
        var extent = HalfExtent * CellSize;

        for (var i = -HalfExtent; i <= HalfExtent; i++)
        {
            var offset = i * CellSize;

            DrawLine(
                gizmos,
                new Vector3(originX + offset, 0f, originZ - extent),
                new Vector3(originX + offset, 0f, originZ + extent),
                originX + offset,
                axisZColor,
                i);

            DrawLine(
                gizmos,
                new Vector3(originX - extent, 0f, originZ + offset),
                new Vector3(originX + extent, 0f, originZ + offset),
                originZ + offset,
                axisXColor,
                i);
        }

        gizmos.Color = previousColor;
        gizmos.Thickness = previousThickness;
    }

    /// <summary>
    /// Draws one grid line, coloring it as the world axis when it passes through the origin and
    /// brightening every <see cref="MajorEvery"/>th line otherwise.
    /// </summary>
    static void DrawLine(
        Gizmos gizmos,
        Vector3 from,
        Vector3 to,
        float coordinate,
        Vector4 axisColor,
        int index)
    {
        if (MathF.Abs(coordinate) < CellSize * 0.5f)
        {
            gizmos.Color = axisColor;
            gizmos.Thickness = 2f;
        }
        else
        {
            gizmos.Color = index % MajorEvery == 0 ? majorColor : minorColor;
            gizmos.Thickness = 1f;
        }

        gizmos.DrawLine(from, to);
    }
}
