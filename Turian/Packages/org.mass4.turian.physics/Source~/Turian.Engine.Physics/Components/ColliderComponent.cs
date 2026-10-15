namespace Turian.Engine.Physics;

/// <summary>
/// A collision shape on a node. Without a <see cref="RigidbodyComponent"/> on the node or an ancestor it is a
/// static collider; otherwise it is part of that body, and several such colliders form one compound body.
/// </summary>
public abstract class ColliderComponent : Component
{
    static long nextId;

    /// <summary>Reports overlaps through <see cref="ITriggerHandler"/> instead of colliding.</summary>
    public bool IsTrigger
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The shape's center in the node's local space.</summary>
    public Vector3 Center
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The surface; null uses the project's default material.</summary>
    public PhysicsMaterial? Material
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>Layers this collider also collides with, beyond the layer collision matrix.</summary>
    public LayerMask IncludeLayers
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>Layers this collider never collides with.</summary>
    public LayerMask ExcludeLayers
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The physics service of the running scene, or null in hosts without physics.</summary>
    /// <remarks>Not private: reflection on a derived collider type cannot see a base class's private setter.</remarks>
    [InjectService(Optional = true), JsonIgnore]
    public PhysicsScene? Physics { get; internal set; }

    /// <summary>The body this collider belongs to: the nearest enabled rigidbody on its node or an ancestor.</summary>
    [JsonIgnore, Hide]
    public RigidbodyComponent? AttachedRigidbody => PhysicsScene.FindRigidbody(Node);

    /// <summary>The world-space bounds of the shape.</summary>
    [JsonIgnore, Hide]
    public Bounds Bounds => ColliderGeometry.WorldBounds(this);

    /// <summary>An identifier unique in the process, reported by the backend in hits and events.</summary>
    [JsonIgnore, Hide]
    public ulong PhysicsId { get; } = (ulong)Interlocked.Increment(ref nextId);

    /// <summary>The point on the shape closest to <paramref name="position"/>; a point inside returns itself.</summary>
    public Vector3 ClosestPoint(Vector3 position) => ColliderGeometry.ClosestPoint(this, position);

    /// <summary>The shape with the node's global scale baked in, centered on the shape's own frame.</summary>
    /// <param name="scale">The node's global scale.</param>
    /// <param name="rotation">The shape's rotation relative to the node.</param>
    public abstract ShapeDesc Describe(Vector3 scale, out Quaternion rotation);

    /// <summary>The shape's pose in world space, from the node's global transform and <see cref="Center"/>.</summary>
    public PhysicsPose WorldPose(out ShapeDesc shape)
    {
        var global = Node?.GlobalTransform ?? Transform.Identity;
        shape = Describe(global.Scale, out var rotation);
        return new PhysicsPose(Vector3.Transform(Center, global.Matrix4X4()),
            Quaternion.Normalize(global.Orientation * rotation));
    }

    /// <inheritdoc />
    public override void OnEnable() => Physics?.Register(this);

    /// <inheritdoc />
    public override void OnDisable() => Physics?.Unregister(this);

    /// <summary>Sets a property and rebuilds the collider's body when the value changed.</summary>
    protected void Change<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Physics?.MarkDirty(this);
    }

    /// <summary>The largest absolute component of a scale.</summary>
    protected static float MaxAbs(Vector3 scale) =>
        MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));

    /// <summary>The rotation turning the Y axis onto the given axis: 0 for X, 1 for Y, 2 for Z.</summary>
    protected static Quaternion AxisRotation(int direction) => direction switch
    {
        0 => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f),
        2 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
        _ => Quaternion.Identity,
    };

    /// <summary>The scale along an axis and the largest scale across it.</summary>
    protected static (float Along, float Across) AxisScale(Vector3 scale, int direction)
    {
        var abs = Vector3.Abs(scale);
        return direction switch
        {
            0 => (abs.X, MathF.Max(abs.Y, abs.Z)),
            2 => (abs.Z, MathF.Max(abs.X, abs.Y)),
            _ => (abs.Y, MathF.Max(abs.X, abs.Z)),
        };
    }
}

/// <summary>A box collider.</summary>
[ComponentContextMenu("Physics/Box Collider")]
[TypeId("6084b6f2-581c-40f2-842b-13768dad5a13")]
public class BoxColliderComponent : ColliderComponent
{
    /// <summary>The box's full extents in the node's local space.</summary>
    public Vector3 Size
    {
        get;
        set => Change(ref field, value);
    } = Vector3.One;

    /// <inheritdoc />
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        return ShapeDesc.Box(Vector3.Abs(Size * scale));
    }
}

/// <summary>A sphere collider.</summary>
[ComponentContextMenu("Physics/Sphere Collider")]
[TypeId("e4ab1a39-10af-4304-87e5-66db1f088ff8")]
public class SphereColliderComponent : ColliderComponent
{
    /// <summary>The radius in the node's local space; the largest scale axis scales it.</summary>
    public float Radius
    {
        get;
        set => Change(ref field, value);
    } = 0.5f;

    /// <inheritdoc />
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        return ShapeDesc.Sphere(MathF.Abs(Radius) * MaxAbs(scale));
    }
}

/// <summary>A capsule collider: a cylinder with hemispherical ends.</summary>
[ComponentContextMenu("Physics/Capsule Collider")]
[TypeId("ffc7fee2-e9cb-47fa-8a8e-f15458dea12e")]
public class CapsuleColliderComponent : ColliderComponent
{
    /// <summary>The radius of the ends and body.</summary>
    public float Radius
    {
        get;
        set => Change(ref field, value);
    } = 0.5f;

    /// <summary>The total height, including both ends.</summary>
    public float Height
    {
        get;
        set => Change(ref field, value);
    } = 2f;

    /// <summary>The local axis along the height: 0 for X, 1 for Y, 2 for Z.</summary>
    public int Direction
    {
        get;
        set => Change(ref field, Math.Clamp(value, 0, 2));
    } = 1;

    /// <inheritdoc />
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = AxisRotation(Direction);
        var (along, across) = AxisScale(scale, Direction);
        var radius = MathF.Abs(Radius) * across;
        return ShapeDesc.Capsule(radius, MathF.Max(0f, MathF.Abs(Height) * along - 2f * radius));
    }
}

/// <summary>A cylinder collider.</summary>
[ComponentContextMenu("Physics/Cylinder Collider")]
[TypeId("52e9c756-387c-4f38-9a0f-46e746b938dd")]
public class CylinderColliderComponent : ColliderComponent
{
    /// <summary>The radius.</summary>
    public float Radius
    {
        get;
        set => Change(ref field, value);
    } = 0.5f;

    /// <summary>The full height.</summary>
    public float Height
    {
        get;
        set => Change(ref field, value);
    } = 2f;

    /// <summary>The local axis along the height: 0 for X, 1 for Y, 2 for Z.</summary>
    public int Direction
    {
        get;
        set => Change(ref field, Math.Clamp(value, 0, 2));
    } = 1;

    /// <inheritdoc />
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = AxisRotation(Direction);
        var (along, across) = AxisScale(scale, Direction);
        return ShapeDesc.Cylinder(MathF.Abs(Radius) * across, MathF.Abs(Height) * along);
    }
}
