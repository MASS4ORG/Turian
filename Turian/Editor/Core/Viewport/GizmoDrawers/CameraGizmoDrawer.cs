namespace Turian.Editor.Core;

/// <summary>
/// Draws a wireframe gizmo for camera components: a small cube at the node origin plus a forward
/// direction ray.
/// </summary>
[CustomGizmo(typeof(CameraGizmoDrawer), typeof(CameraComponent))]
public sealed class CameraGizmoDrawer : IGizmoDrawer
{
    static readonly Vector4 bodyColor = new(0.3f, 0.75f, 1f, 1f);

    /// <inheritdoc/>
    public void DrawGizmos(Gizmos gizmos, Component component)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        if (component is not CameraComponent camera || camera.Node is null) return;

        var tfm = camera.Node.GlobalTransform;
        gizmos.Color = bodyColor;
        gizmos.Thickness = 1.2f;
        gizmos.DrawWireCube(tfm.Position, new Vector3(0.35f, 0.25f, 0.4f));

        var forward = camera.Front;
        var p1 = tfm.Position + forward * 0.6f;
        var p2 = p1 + forward * 0.3f;
        gizmos.DrawLine(p1, p2);

        var (u, v) = OrthonormalBasis(forward);
        var size = 0.12f;
        gizmos.DrawLine(p2, p2 - forward * size + u * size);
        gizmos.DrawLine(p2, p2 - forward * size - u * size);
        gizmos.DrawLine(p2, p2 - forward * size + v * size);
        gizmos.DrawLine(p2, p2 - forward * size - v * size);
    }

    static (Vector3 U, Vector3 V) OrthonormalBasis(Vector3 normal)
    {
        normal = Vector3.Normalize(normal);
        var fallback = MathF.Abs(normal.Y) < 0.99f ? Mathf.Up : Mathf.Right;
        var u = Vector3.Normalize(Vector3.Cross(normal, fallback));
        var v = Vector3.Cross(normal, u);
        return (u, v);
    }
}
