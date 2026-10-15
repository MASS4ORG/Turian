namespace Turian.Engine.Physics;

/// <summary>
/// Draws a rigidbody's center of mass and, while simulating, its velocity and the contacts of its last step: a red
/// dot with its normal at each point.
/// </summary>
[CustomGizmo(typeof(RigidbodyGizmoDrawer), typeof(RigidbodyComponent))]
public sealed class RigidbodyGizmoDrawer : IGizmoDrawer
{
    const float MarkerSize = 0.05f;
    const float NormalLength = 0.25f;
    const float VelocityScale = 0.1f;

    /// <summary>The color of contact points and their normals.</summary>
    public static readonly Vector4 ContactColor = new(1f, 0.3f, 0.25f, 1f);

    /// <summary>The color of the center of mass and velocity.</summary>
    public static readonly Vector4 BodyColor = new(1f, 1f, 1f, 1f);

    /// <inheritdoc />
    public void DrawGizmos(Gizmos gizmos, Component component)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        if (component is not RigidbodyComponent { Node: not null } body)
        {
            return;
        }

        var center = body.WorldCenterOfMass;
        gizmos.Color = BodyColor;
        DrawMarker(gizmos, center);
        if (body.Physics is not { IsSimulating: true } physics)
        {
            return;
        }

        gizmos.DrawRay(center, body.LinearVelocity * VelocityScale);
        gizmos.Color = ContactColor;
        foreach (var contact in physics.LastContacts)
        {
            if (contact.ThisCollider.AttachedRigidbody == body || contact.OtherCollider.AttachedRigidbody == body)
            {
                DrawMarker(gizmos, contact.Point);
                gizmos.DrawRay(contact.Point, contact.Normal * NormalLength);
            }
        }
    }

    static void DrawMarker(Gizmos gizmos, Vector3 point)
    {
        gizmos.DrawLine(point - new Vector3(MarkerSize, 0, 0), point + new Vector3(MarkerSize, 0, 0));
        gizmos.DrawLine(point - new Vector3(0, MarkerSize, 0), point + new Vector3(0, MarkerSize, 0));
        gizmos.DrawLine(point - new Vector3(0, 0, MarkerSize), point + new Vector3(0, 0, MarkerSize));
    }
}
