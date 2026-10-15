namespace Turian.Engine.Physics;

/// <summary>Draws a joint's anchor, its axis when it has one, and a line to the connected anchor.</summary>
[CustomGizmo(typeof(JointGizmoDrawer), typeof(JointComponent))]
public sealed class JointGizmoDrawer : IGizmoDrawer
{
    const float MarkerSize = 0.08f;
    const float AxisLength = 0.5f;

    /// <summary>The color of joint anchors and axes.</summary>
    public static readonly Vector4 JointColor = new(1f, 0.55f, 0.1f, 1f);

    /// <inheritdoc />
    public void DrawGizmos(Gizmos gizmos, Component component)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        if (component is not JointComponent { Node: { } own } joint)
        {
            return;
        }

        gizmos.Color = JointColor;
        var anchor = Vector3.Transform(joint.Anchor, own.GlobalTransform.Matrix4X4());
        gizmos.DrawWireCube(anchor, new Vector3(MarkerSize));
        _ = joint.Describe(out var axis);
        if (joint is HingeJointComponent or SliderJointComponent or BallSocketJointComponent)
        {
            gizmos.DrawLine(anchor - axis * AxisLength, anchor + axis * AxisLength);
        }

        var connected = joint.ConnectedBody?.Node is { } node
            ? Vector3.Transform(joint.ConnectedAnchor, node.GlobalTransform.Matrix4X4())
            : joint.ConnectedAnchor;
        // An automatic connected anchor is only known once the joint exists.
        if (!joint.AutoConfigureConnectedAnchor || joint.Physics?.IsCreated(joint) == true)
        {
            gizmos.DrawLine(anchor, connected);
        }
    }
}
