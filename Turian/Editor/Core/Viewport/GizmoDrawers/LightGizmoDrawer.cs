namespace Turian.Editor.Core;

/// <summary>
/// Draws a wireframe gizmo for light components: a sphere whose radius scales with the light's
/// intensity, tinted by the light color.
/// </summary>
[CustomGizmo(typeof(LightGizmoDrawer), typeof(LightComponent))]
public sealed class LightGizmoDrawer : IGizmoDrawer
{
    /// <inheritdoc/>
    public void DrawGizmos(Gizmos gizmos, Component component)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        if (component is not LightComponent light || light.Node is null) return;

        var color = light.Color;
        gizmos.Color = new Vector4(color.X, color.Y, color.Z, 1f);
        gizmos.Thickness = 1.5f;

        var position = light.Node.GlobalTransform.Position;
        var radius = 0.25f + Math.Clamp(light.Intensity, 0f, 10f) * 0.15f;
        gizmos.DrawWireSphere(position, radius);

        // Center marker.
        gizmos.DrawLine(position - new Vector3(0.05f, 0, 0), position + new Vector3(0.05f, 0, 0));
        gizmos.DrawLine(position - new Vector3(0, 0.05f, 0), position + new Vector3(0, 0.05f, 0));
        gizmos.DrawLine(position - new Vector3(0, 0, 0.05f), position + new Vector3(0, 0, 0.05f));
    }
}
