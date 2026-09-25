namespace Turian.Engine.Core;

/// <summary>
/// Immediate-mode gizmo drawing. Lines submitted during a frame are rendered by
/// <see cref="GizmoRenderSystem"/> as part of the same render pass as the scene.
///
/// <para>One instance is shared per Scene View (see <c>SceneViewerService.Gizmos</c>). The buffer is
/// cleared at the start of every frame, so anything not redrawn disappears.</para>
/// </summary>
public sealed class Gizmos
{
    /// <summary>The maximum number of line segments that can be recorded per frame.</summary>
    public const int MaxLines = 32_768;

    readonly List<GizmoLine> worldLines = new(MaxLines);
    readonly List<GizmoLine> overlayLines = new(MaxLines);

    /// <summary>Gets the segments recorded with { <see cref="DepthTest"/> == true }.</summary>
    public IReadOnlyList<GizmoLine> WorldLines => worldLines;

    /// <summary>Gets the segments recorded with { <see cref="DepthTest"/> == false }.</summary>
    public IReadOnlyList<GizmoLine> OverlayLines => overlayLines;

    /// <summary>Gets the number of lines recorded this frame.</summary>
    public int LineCount => worldLines.Count + overlayLines.Count;

    /// <summary>
    /// Gets a value indicating whether <see cref="MaxLines"/> was exceeded this frame.
    /// When overflow occurs, only the first <see cref="MaxLines"/> lines are rendered.
    /// </summary>
    public bool IsOverflow { get; private set; }

    /// <summary>Gets or sets the color applied to primitives drawn while it is active.</summary>
    public Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);

    /// <summary>Gets or sets the thickness, in screen pixels, of drawn lines.</summary>
    public float Thickness { get; set; } = 1f;

    /// <summary>
    /// Gets or sets the world-to-gizmo-space transform applied to every primitive.
    /// Defaults to identity (world space); set to a node's transform for local-space drawing.
    /// </summary>
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    /// <summary>
    /// Gets or sets whether lines recorded while it is set draw against the scene's depth buffer
    /// (occluded by geometry; the world pass) or on top of everything (the overlay pass).
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool DepthTest { get; set; } = true;

    /// <summary>Clears all lines recorded for the previous frame. Called once per frame before drawing.</summary>
    public void Clear()
    {
        worldLines.Clear();
        overlayLines.Clear();
        IsOverflow = false;
    }

    /// <summary>
    /// Draws a line between <paramref name="from"/> and <paramref name="to"/>.
    /// The endpoints are transformed by <see cref="Matrix"/> using the current <see cref="Color"/> and
    /// <see cref="Thickness"/>.
    /// </summary>
    public void DrawLine(Vector3 from, Vector3 to)
    {
        if (LineCount >= MaxLines)
        {
            IsOverflow = true;
            return;
        }

        var line = new GizmoLine(
            Vector3.Transform(from, Matrix),
            Vector3.Transform(to, Matrix),
            Color,
            Thickness);
        (DepthTest ? worldLines : overlayLines).Add(line);
    }

    /// <summary>Draws a ray starting at <paramref name="from"/> pointing along <paramref name="direction"/>.</summary>
    public void DrawRay(Vector3 from, Vector3 direction) =>
        DrawLine(from, from + direction);

    /// <summary>Draws a wireframe cuboid centered on <paramref name="center"/> with the given <paramref name="size"/>.</summary>
    public void DrawWireCube(Vector3 center, Vector3 size)
    {
        var h = size * 0.5f;
        var x = new Vector3(h.X, 0f, 0f);
        var y = new Vector3(0f, h.Y, 0f);
        var z = new Vector3(0f, 0f, h.Z);

        Span<Vector3> p =
        [
            center - x - y - z, center + x - y - z, center + x + y - z, center - x + y - z,
            center - x - y + z, center + x - y + z, center + x + y + z, center - x + y + z,
        ];

        // Bottom face, top face, verticals.
        DrawLine(p[0], p[1]);
        DrawLine(p[1], p[2]);
        DrawLine(p[2], p[3]);
        DrawLine(p[3], p[0]);
        DrawLine(p[4], p[5]);
        DrawLine(p[5], p[6]);
        DrawLine(p[6], p[7]);
        DrawLine(p[7], p[4]);
        DrawLine(p[0], p[4]);
        DrawLine(p[1], p[5]);
        DrawLine(p[2], p[6]);
        DrawLine(p[3], p[7]);
    }

    /// <summary>
    /// Draws a wireframe cuboid aligned to <paramref name="cubeMatrix"/>. The 2×2×2 cube centred on the
    /// origin is transformed by <c>Matrix * cubeMatrix</c>.
    /// </summary>
    public void DrawWireCube(Matrix4x4 cubeMatrix)
    {
        var old = Matrix;
        Matrix = old * cubeMatrix;
        DrawWireCube(Vector3.Zero, new Vector3(2f, 2f, 2f));
        Matrix = old;
    }

    /// <summary>
    /// Draws a wireframe sphere around <paramref name="center"/>. Three circles are drawn in the X,
    /// Y and Z planes of gizmo space for an unambiguous 3D outline.
    /// </summary>
    public void DrawWireSphere(Vector3 center, float radius)
    {
        DrawCircle(center, Mathf.Right, radius);
        DrawCircle(center, Mathf.Up, radius);
        DrawCircle(center, Mathf.Forward, radius);
    }

    /// <summary>
    /// Draws a circle in the plane perpendicular to <paramref name="normal"/>, centred on
    /// <paramref name="center"/>.
    /// </summary>
    public void DrawCircle(Vector3 center, Vector3 normal, float radius)
    {
        var (u, v) = OrthonormalBasis(normal);
        var previous = center + (u * radius);
        for (var i = 1; i <= CircleSegments; i++)
        {
            var angle = (MathF.PI * 2f * i) / CircleSegments;
            var current = center + ((u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius);
            DrawLine(previous, current);
            previous = current;
        }
    }

    /// <summary>
    /// Draws an arc of <paramref name="angle"/> degrees starting at <paramref name="fromDirection"/>
    /// around <paramref name="normal"/>, in the plane perpendicular to <paramref name="normal"/>.
    /// </summary>
    public void DrawArc(
        Vector3 center,
        Vector3 normal,
        Vector3 fromDirection,
        float angle,
        float radius)
    {
        var (u, v) = OrthonormalBasis(normal);
        var startU = Vector3.Dot(Vector3.Normalize(fromDirection), u);
        var startV = Vector3.Dot(Vector3.Normalize(fromDirection), v);
        var startAngle = MathF.Atan2(startV, startU);

        var endAngle = startAngle + (angle * MathF.PI / 180f);
        var steps = Math.Max(2, CircleSegments * (int)MathF.Ceiling(MathF.Abs(angle) / 360f));
        var previous = center + ((u * MathF.Cos(startAngle) + v * MathF.Sin(startAngle)) * radius);
        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            var a = startAngle + ((endAngle - startAngle) * t);
            var current = center + ((u * MathF.Cos(a) + v * MathF.Sin(a)) * radius);
            DrawLine(previous, current);
            previous = current;
        }
    }

    /// <summary>Number of segments used to approximate circles and arcs.</summary>
    public const int CircleSegments = 64;

    /// <summary>
    /// Builds an orthonormal basis for a plane with the given normal. The first vector is an
    /// arbitrary vector perpendicular to <paramref name="normal"/>; the second is the cross product.
    /// </summary>
    static (Vector3 U, Vector3 V) OrthonormalBasis(Vector3 normal)
    {
        normal = Vector3.Normalize(normal);
        var fallback = MathF.Abs(normal.Y) < 0.99f ? Mathf.Up : Mathf.Right;
        var u = Vector3.Normalize(Vector3.Cross(normal, fallback));
        if (Vector3.Dot(u, u) < 1e-9f)
        {
            u = new Vector3(0f, 0f, 1f);
        }

        var v = Vector3.Cross(normal, u);
        return (u, v);
    }
}
