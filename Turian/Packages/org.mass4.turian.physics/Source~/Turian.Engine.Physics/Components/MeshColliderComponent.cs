namespace Turian.Engine.Physics;

/// <summary>
/// A collider shaped like a mesh: the mesh's convex hull, or its triangles for static and kinematic geometry. Without
/// a <see cref="Mesh"/> or <see cref="Model"/> it uses those of a <see cref="ModelComponent"/> on the same node.
/// </summary>
/// <remarks>Triangle meshes are one-sided and the backend ignores them on dynamic bodies; use <see cref="Convex"/>.</remarks>
[ComponentContextMenu("Physics/Mesh Collider")]
[TypeId("14f79059-b706-4fd1-80a7-b3665e0c755e")]
public class MeshColliderComponent : ColliderComponent
{
    (Guid Model, Guid Mesh) loadedIds;
    MeshGeometry? loaded;
    MeshGeometry? custom;

    /// <summary>Whether the collider is the mesh's convex hull, which dynamic bodies can use.</summary>
    public bool Convex
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The mesh whose triangles form the collider; it takes precedence over <see cref="Model"/>.</summary>
    public AssetReference<MeshAsset>? Mesh
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The model whose every submesh forms the collider.</summary>
    public AssetReference<ModelAsset>? Model
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The asset database meshes are read from, or null outside a project.</summary>
    [InjectService(Optional = true), JsonIgnore]
    public AssetDatabase? Assets { get; internal set; }

    /// <inheritdoc />
    [JsonIgnore, Hide]
    public override bool HasShape => Geometry() is not null;

    /// <summary>
    /// Uses script-built geometry, such as a procedural terrain, instead of a mesh asset until cleared with null.
    /// </summary>
    /// <param name="vertices">The vertices in the node's local space.</param>
    /// <param name="indices">Three indices per counter-clockwise triangle.</param>
    public void SetGeometry(Vector3[]? vertices, int[]? indices = null)
    {
        custom = vertices is null ? null : new MeshGeometry(vertices, indices ?? []);
        Physics?.MarkDirty(this);
    }

    /// <inheritdoc />
    /// <remarks>Without geometry it is an empty hull, which is never simulated; see <see cref="HasShape"/>.</remarks>
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        if (Geometry() is not { } geometry)
        {
            return ShapeDesc.ConvexHull([]);
        }

        var points = new Vector3[geometry.Vertices.Length];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = geometry.Vertices[i] * scale;
        }

        // A negative scale mirrors the mesh, which reverses its winding.
        var indices = scale.X * scale.Y * scale.Z < 0f ? MeshGeometry.Flip(geometry.Indices) : geometry.Indices;
        return Convex || indices.Length < 3 ? ShapeDesc.ConvexHull(points) : ShapeDesc.TriangleMesh(points, indices);
    }

    /// <summary>The geometry in use: the script's, the referenced mesh's or model's, or the sibling model's.</summary>
    internal MeshGeometry? Geometry()
    {
        if (custom is not null)
        {
            return custom;
        }

        var ids = SourceIds();
        if (ids != loadedIds)
        {
            loadedIds = ids;
            loaded = Assets is { } assets ? MeshGeometry.Read(assets, ids.Model, ids.Mesh) : null;
        }

        return loaded;
    }

    (Guid Model, Guid Mesh) SourceIds()
    {
        if (Mesh is { IsEmpty: false } || Model is { IsEmpty: false })
        {
            return (Model?.AssetId ?? Guid.Empty, Mesh?.AssetId ?? Guid.Empty);
        }

        var sibling = Node?.GetComponent<ModelComponent>();
        return (sibling?.Model?.AssetId ?? Guid.Empty, sibling?.Mesh?.AssetId ?? Guid.Empty);
    }
}
