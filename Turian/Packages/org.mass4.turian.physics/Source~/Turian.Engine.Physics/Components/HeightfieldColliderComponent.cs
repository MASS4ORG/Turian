namespace Turian.Engine.Physics;

/// <summary>
/// Static terrain from a square grid of heights, as Unity's terrain collider: the grid spans <see cref="Size"/> from
/// the node's origin along +X and +Z, and each height from 0 to 1 is scaled by <see cref="Size"/>'s Y.
/// </summary>
/// <remarks>It becomes a one-sided triangle mesh facing up, which the backend ignores on dynamic bodies.</remarks>
[ComponentContextMenu("Physics/Heightfield Collider")]
[TypeId("5b3e9a1c-7d42-4f8e-9c6a-2e1f0b7d4a93")]
public class HeightfieldColliderComponent : ColliderComponent
{
    /// <summary>Samples per side; the grid holds its square.</summary>
    public int Resolution
    {
        get;
        set => Change(ref field, Math.Max(value, 2));
    } = 2;

    /// <summary>The terrain's width (X), height of a sample of 1 (Y) and length (Z), in local units.</summary>
    public Vector3 Size
    {
        get;
        set => Change(ref field, value);
    } = new(10f, 1f, 10f);

    /// <summary>Heights from 0 to 1, row by row along Z, each row along X: index <c>z * Resolution + x</c>.</summary>
    public List<float> Heights
    {
        get;
        set => Change(ref field, value ?? []);
    } = [];

    /// <inheritdoc />
    [JsonIgnore, Hide]
    public override bool HasShape => Heights.Count >= Resolution * Resolution;

    /// <summary>Replaces the grid with <paramref name="heights"/>, indexed <c>[z, x]</c> as in Unity.</summary>
    /// <param name="heights">A square array of heights from 0 to 1.</param>
    public void SetHeights(float[,] heights)
    {
        ArgumentNullException.ThrowIfNull(heights);
        var resolution = heights.GetLength(0);
        if (resolution != heights.GetLength(1) || resolution < 2)
        {
            throw new ArgumentException("Heights must be a square grid of at least 2 by 2 samples.", nameof(heights));
        }

        var values = new List<float>(resolution * resolution);
        for (var z = 0; z < resolution; z++)
        {
            for (var x = 0; x < resolution; x++)
            {
                values.Add(heights[z, x]);
            }
        }

        Resolution = resolution;
        Heights = values;
    }

    /// <summary>
    /// The terrain's height under a world position, relative to the node's origin in world units, as Unity's
    /// SampleHeight; positions outside the grid use its nearest edge.
    /// </summary>
    public float SampleHeight(Vector3 worldPosition)
    {
        if (!HasShape || Node is not { } node || !Matrix4x4.Invert(node.GlobalTransform.Matrix4X4(), out var inverse))
        {
            return 0f;
        }

        var local = Vector3.Transform(worldPosition, inverse);
        var cells = Resolution - 1;
        var u = Math.Clamp(local.X / Size.X * cells, 0f, cells);
        var v = Math.Clamp(local.Z / Size.Z * cells, 0f, cells);
        var (x, z) = (Math.Min((int)u, cells - 1), Math.Min((int)v, cells - 1));
        var (fx, fz) = (u - x, v - z);
        var near = float.Lerp(Height(x, z), Height(x + 1, z), fx);
        var far = float.Lerp(Height(x, z + 1), Height(x + 1, z + 1), fx);
        var surface = Vector3.Transform(local with { Y = float.Lerp(near, far, fz) * Size.Y },
            node.GlobalTransform.Matrix4X4());
        return surface.Y - node.GlobalTransform.Position.Y;
    }

    /// <inheritdoc />
    /// <remarks>Without enough heights it is an empty hull, never simulated; see <see cref="HasShape"/>.</remarks>
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        if (!HasShape)
        {
            return ShapeDesc.ConvexHull([]);
        }

        var resolution = Resolution;
        var step = new Vector2(Size.X, Size.Z) / (resolution - 1);
        var points = new Vector3[resolution * resolution];
        for (var z = 0; z < resolution; z++)
        {
            for (var x = 0; x < resolution; x++)
            {
                points[z * resolution + x] = new Vector3(x * step.X, Height(x, z) * Size.Y, z * step.Y) * scale;
            }
        }

        var indices = new int[(resolution - 1) * (resolution - 1) * 6];
        var i = 0;
        for (var z = 0; z < resolution - 1; z++)
        {
            for (var x = 0; x < resolution - 1; x++)
            {
                // Two counter-clockwise triangles per cell, seen from above.
                var corner = z * resolution + x;
                (indices[i], indices[i + 1], indices[i + 2]) = (corner, corner + resolution, corner + 1);
                (indices[i + 3], indices[i + 4], indices[i + 5]) =
                    (corner + 1, corner + resolution, corner + resolution + 1);
                i += 6;
            }
        }

        // A negative scale mirrors the grid, which reverses its winding.
        return ShapeDesc.TriangleMesh(points, scale.X * scale.Y * scale.Z < 0f ? MeshGeometry.Flip(indices) : indices);
    }

    float Height(int x, int z) => Heights[z * Resolution + x];
}
