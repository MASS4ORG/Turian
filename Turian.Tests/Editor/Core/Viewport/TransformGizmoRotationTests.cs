namespace Turian.Tests;

/// <summary>Exercises rotation ring drags through the viewport pointer API.</summary>
public class TransformGizmoRotationTests
{
    static readonly Vector2 Viewport = new(960f, 540f);
    static float Radius => 2f * 5f * 90f / (MathF.Sqrt(3f) * 540f) * 0.85f;

    /// <summary>Each rotation ring rotates its axis without changing position or scale.</summary>
    [Theory]
    [InlineData(TransformGizmoAxis.X)]
    [InlineData(TransformGizmoAxis.Y)]
    [InlineData(TransformGizmoAxis.Z)]
    public void RotationDrag_RotatesSelectedAxis(TransformGizmoAxis axis)
    {
        var normal = AxisDirection(axis);
        var (camera, node, gizmo) = Create(normal);
        var position = node.Position;
        var scale = node.Scale;
        var (u, v) = Basis(normal);
        var edits = 0;
        gizmo.TransformEdited += () => edits++;

        Drag(gizmo, camera, node.Position, u, v, MathF.PI / 3f);

        Assert.Equal(axis, gizmo.Axis);
        AssertRotation(Quaternion.CreateFromAxisAngle(normal, MathF.PI / 3f), node.Orientation);
        Assert.Equal(position, node.Position);
        Assert.Equal(scale, node.Scale);
        Assert.Equal(1, edits);
        gizmo.ProcessPointerUp();
        Assert.False(gizmo.IsDragging);
    }

    /// <summary>Rotation snapping rounds the total angle measured from the start of the gesture.</summary>
    [Theory]
    [InlineData(22f, 15f)]
    [InlineData(-22f, -15f)]
    public void RotationDrag_SnapsInDegrees(float angle, float expected)
    {
        var (camera, node, gizmo) = Create(Vector3.UnitZ);
        gizmo.SnapRotation = 15f;
        var (u, v) = Basis(Vector3.UnitZ);

        Drag(gizmo, camera, node.Position, u, v, angle * MathF.PI / 180f);

        AssertRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, expected * MathF.PI / 180f),
            node.Orientation);
    }

    /// <summary>Small orthographic rings choose the nearest ring instead of the first axis within tolerance.</summary>
    [Fact]
    public void RotationDrag_OrthographicCamera_SelectsNearestRing()
    {
        var (camera, node, gizmo) = Create(Vector3.UnitZ);
        camera.IsOrthographic = true;
        camera.Frustum = 10f;
        var (u, v) = Basis(Vector3.UnitZ);
        var radius = 20f * 90f / 540f * 0.85f;
        gizmo.ProcessPointerDown(Pixel(camera, (u * MathF.Cos(0.6f) + v * MathF.Sin(0.6f)) * radius), camera, Viewport);
        gizmo.ProcessPointerMove(Pixel(camera, (u * MathF.Cos(1f) + v * MathF.Sin(1f)) * radius), camera, Viewport);

        Assert.Equal(TransformGizmoAxis.Z, gizmo.Axis);
        AssertRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f), node.Orientation);
    }

    /// <summary>World and local ring axes account for both the node and its rotated parent.</summary>
    [Theory]
    [InlineData(TransformGizmoSpace.World)]
    [InlineData(TransformGizmoSpace.Local)]
    public void RotationDrag_WithRotatedParent_UsesSelectedSpace(TransformGizmoSpace space)
    {
        var parent = new Node { Orientation = Quaternion.CreateFromYawPitchRoll(0.7f, 0.4f, 0.2f) };
        var node = new Node
        {
            Parent = parent,
            Orientation = Quaternion.CreateFromYawPitchRoll(-0.2f, 0.3f, 0.1f),
        };
        var start = node.GlobalTransform.Orientation;
        var normal = space == TransformGizmoSpace.Local ? Vector3.Transform(Vector3.UnitZ, start) : Vector3.UnitZ;
        var (camera, _, gizmo) = Create(normal);
        gizmo.SelectedNode = node;
        gizmo.Space = space;
        var (u, v) = Basis(normal);

        Drag(gizmo, camera, node.GlobalTransform.Position, u, v, 0.4f);

        Assert.Equal(TransformGizmoAxis.Z, gizmo.Axis);
        AssertRotation(Quaternion.CreateFromAxisAngle(normal, 0.4f) * start, node.GlobalTransform.Orientation);
    }

    /// <summary>Small consecutive rotations accumulate across the angle wrap and beyond a full turn.</summary>
    [Fact]
    public void RotationDrag_CrossesAngleWrapAndAccumulates()
    {
        var (camera, node, gizmo) = Create(Vector3.UnitZ);
        var (u, v) = Basis(Vector3.UnitZ);
        gizmo.ProcessPointerDown(Pixel(camera, RingPoint(u, v, 0.6f)), camera, Viewport);
        Assert.True(gizmo.IsDragging);

        for (var i = 1; i <= 40; i++)
            gizmo.ProcessPointerMove(Pixel(camera, RingPoint(u, v, 0.6f + i * 0.2f)), camera, Viewport);

        AssertRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 8f), node.Orientation);
    }

    /// <summary>A ring viewed edge-on still rotates when dragged along its projected tangent.</summary>
    [Fact]
    public void RotationDrag_EdgeOnRing_Rotates()
    {
        var (camera, node, gizmo) = Create(Vector3.UnitZ);
        var start = Pixel(camera, new Vector3(0f, Radius * 0.6f, 0f));
        gizmo.ProcessPointerDown(start, camera, Viewport);
        Assert.Equal(TransformGizmoAxis.X, gizmo.Axis);

        gizmo.ProcessPointerMove(start + new Vector2(0f, 15f), camera, Viewport);

        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Identity, node.Orientation)) < 0.999f);
        Assert.Equal(0f, node.Orientation.Y, 4);
        Assert.Equal(0f, node.Orientation.Z, 4);
    }

    /// <summary>A pointer at the pivot has no angular direction and leaves the rotation finite.</summary>
    [Fact]
    public void RotationDrag_AtPivot_DoesNotChangeOrientation()
    {
        var (camera, node, gizmo) = Create(Vector3.UnitZ);
        var (u, v) = Basis(Vector3.UnitZ);
        gizmo.ProcessPointerDown(Pixel(camera, RingPoint(u, v, 0.6f)), camera, Viewport);
        var orientation = node.Orientation;

        gizmo.ProcessPointerMove(Pixel(camera, Vector3.Zero), camera, Viewport);

        Assert.Equal(orientation, node.Orientation);
    }

    static (EditorCamera Camera, Node Node, TransformGizmo Gizmo) Create(Vector3 normal)
    {
        var camera = new EditorCamera { Position = -normal * 5f };
        camera.Resize(960, 540);
        var (_, up) = Basis(normal);
        camera.LookIn(normal, up);
        var node = new Node { Scale = new Vector3(2f, 3f, 4f) };
        var gizmo = new TransformGizmo { SelectedNode = node, Mode = TransformGizmoMode.Rotate, SnapRotation = 0f };
        return (camera, node, gizmo);
    }

    static Vector3 AxisDirection(TransformGizmoAxis axis) => axis switch
    {
        TransformGizmoAxis.X => Vector3.UnitX,
        TransformGizmoAxis.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    static (Vector3 U, Vector3 V) Basis(Vector3 normal)
    {
        var reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(normal, reference));
        return (u, Vector3.Cross(normal, u));
    }

    static void Drag(TransformGizmo gizmo, EditorCamera camera, Vector3 anchor, Vector3 u, Vector3 v, float angle)
    {
        gizmo.ProcessPointerDown(Pixel(camera, anchor + RingPoint(u, v, 0.6f)), camera, Viewport);
        Assert.True(gizmo.IsDragging);
        gizmo.ProcessPointerMove(Pixel(camera, anchor + RingPoint(u, v, 0.6f + angle)), camera, Viewport);
    }

    static Vector3 RingPoint(Vector3 u, Vector3 v, float angle) =>
        (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * Radius;

    static Vector2 Pixel(EditorCamera camera, Vector3 point) =>
        (camera.Project(point) + Vector2.One) * 0.5f * Viewport;

    static void AssertRotation(Quaternion expected, Quaternion actual) =>
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), actual)) > 0.99999f,
            $"Expected {expected}, actual {actual}");
}
