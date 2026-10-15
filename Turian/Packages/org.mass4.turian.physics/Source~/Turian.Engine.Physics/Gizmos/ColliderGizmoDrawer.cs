namespace Turian.Engine.Physics;

/// <summary>
/// Draws every collider's shape as a wireframe: green when static, blue when part of a dynamic body, purple when
/// kinematic and yellow for triggers.
/// </summary>
[CustomGizmo(typeof(ColliderGizmoDrawer), typeof(ColliderComponent))]
public sealed class ColliderGizmoDrawer : IGizmoDrawer
{
    // Larger meshes draw only their bounds, keeping the editor responsive.
    const int MaxMeshTriangles = 4096;

    /// <summary>The color of static colliders.</summary>
    public static readonly Vector4 StaticColor = new(0.45f, 0.95f, 0.45f, 1f);

    /// <summary>The color of colliders on dynamic bodies.</summary>
    public static readonly Vector4 DynamicColor = new(0.45f, 0.75f, 1f, 1f);

    /// <summary>The color of colliders on kinematic bodies.</summary>
    public static readonly Vector4 KinematicColor = new(0.8f, 0.5f, 1f, 1f);

    /// <summary>The color of trigger colliders.</summary>
    public static readonly Vector4 TriggerColor = new(1f, 0.85f, 0.3f, 1f);

    /// <inheritdoc />
    public void DrawGizmos(Gizmos gizmos, Component component)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        if (component is not ColliderComponent { Node: not null } collider || !collider.HasShape)
        {
            return;
        }

        var pose = collider.WorldPose(out var shape);
        var old = gizmos.Matrix;
        gizmos.Color = ColorOf(collider);
        gizmos.Matrix = Matrix4x4.CreateFromQuaternion(pose.Orientation) * Matrix4x4.CreateTranslation(pose.Position) * old;
        Draw(gizmos, shape);
        gizmos.Matrix = old;
    }

    /// <summary>The color a collider is drawn with.</summary>
    public static Vector4 ColorOf(ColliderComponent collider)
    {
        ArgumentNullException.ThrowIfNull(collider);
        return collider.IsTrigger ? TriggerColor
            : collider.AttachedRigidbody is not { } body ? StaticColor
            : body.IsKinematic ? KinematicColor
            : DynamicColor;
    }

    // Draws a shape in its own frame, where capsules and cylinders run along Y.
    static void Draw(Gizmos gizmos, in ShapeDesc shape)
    {
        switch (shape.Type)
        {
            case ShapeType.Box:
                gizmos.DrawWireCube(Vector3.Zero, shape.Size);
                break;
            case ShapeType.Sphere:
                gizmos.DrawWireSphere(Vector3.Zero, shape.Radius);
                break;
            case ShapeType.Capsule:
                DrawCapsule(gizmos, shape.Radius, shape.Length * 0.5f);
                break;
            case ShapeType.Cylinder:
                DrawCylinder(gizmos, shape.Radius, shape.Length * 0.5f);
                break;
            default:
                DrawMesh(gizmos, shape);
                break;
        }
    }

    static void DrawCapsule(Gizmos gizmos, float radius, float half)
    {
        DrawCylinder(gizmos, radius, half);
        foreach (var sign in new[] { 1f, -1f })
        {
            foreach (var across in new[] { Vector3.UnitX, Vector3.UnitZ })
            {
                DrawHalfCircle(gizmos, new Vector3(0, sign * half, 0), across * radius, new Vector3(0, sign * radius, 0));
            }
        }
    }

    // The half circle from +across through +up to -across.
    static void DrawHalfCircle(Gizmos gizmos, Vector3 center, Vector3 across, Vector3 up)
    {
        const int segments = Gizmos.CircleSegments / 2;
        var previous = center + across;
        for (var i = 1; i <= segments; i++)
        {
            var angle = MathF.PI * i / segments;
            var current = center + across * MathF.Cos(angle) + up * MathF.Sin(angle);
            gizmos.DrawLine(previous, current);
            previous = current;
        }
    }

    static void DrawCylinder(Gizmos gizmos, float radius, float half)
    {
        var top = new Vector3(0, half, 0);
        gizmos.DrawCircle(top, Vector3.UnitY, radius);
        gizmos.DrawCircle(-top, Vector3.UnitY, radius);
        foreach (var side in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
        {
            gizmos.DrawLine(side * radius - top, side * radius + top);
        }
    }

    static void DrawMesh(Gizmos gizmos, in ShapeDesc shape)
    {
        var points = shape.Points ?? [];
        if (shape.Indices is { Length: >= 3 } indices && indices.Length / 3 <= MaxMeshTriangles)
        {
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                Vector3 a = points[indices[i]], b = points[indices[i + 1]], c = points[indices[i + 2]];
                gizmos.DrawLine(a, b);
                gizmos.DrawLine(b, c);
                gizmos.DrawLine(c, a);
            }

            return;
        }

        if (points.Length > 0)
        {
            var bounds = points.Aggregate(Bounds.Empty, static (box, point) => box.Encapsulate(point));
            gizmos.DrawWireCube(bounds.Center, bounds.Size);
        }
    }
}
