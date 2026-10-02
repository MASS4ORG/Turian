namespace Turian.Tests;

/// <summary>Tests the value-type <see cref="Transform"/> and the node's cached global transform.</summary>
public class TransformTests
{
    static readonly Transform Parent = new()
    {
        Position = new(1, 2, 3),
        Rotation = new(30, 60, 10),
        Scale = new(1, 2, 3),
    };

    static readonly Transform Child = new()
    {
        Position = new(4, -1, 2),
        Rotation = new(-20, 45, 70),
        Scale = new(2, 1, 0.5f),
    };

    static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"Expected {expected}, got {actual}");

    static void AssertClose(Quaternion expected, Quaternion actual) =>
        Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(expected, actual)) - 1f) < 1e-5f,
            $"Expected {expected}, got {actual}");

    /// <summary>A new transform is the identity, not the zeroed struct default.</summary>
    [Fact]
    public void NewTransformIsIdentity()
    {
        var transform = new Transform();

        Assert.Equal(Quaternion.Identity, transform.Orientation);
        Assert.Equal(Vector3.One, transform.Scale);
        Assert.True(transform.IsIdentity);
    }

    /// <summary>The quaternion-built matrix matches the yaw-pitch-roll formula it replaced.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(35, -45, 0)]
    [InlineData(10, 20, 30)]
    [InlineData(-60, 120, 45)]
    public void Matrix4X4MatchesEulerFormula(float x, float y, float z)
    {
        var transform = new Transform { Position = new(1, 2, 3), Rotation = new(x, y, z), Scale = new(2, 3, 4) };
        var r = transform.Rotation * (MathF.PI / 180f);
        var (cX, sX, cY, sY, cZ, sZ) = (MathF.Cos(r.X), MathF.Sin(r.X), MathF.Cos(r.Y), MathF.Sin(r.Y),
            MathF.Cos(r.Z), MathF.Sin(r.Z));
        var s = transform.Scale;
        var expected = new Matrix4x4(
            s.X * ((cY * cZ) + (sY * sX * sZ)), s.X * (cX * sZ), s.X * ((cY * sX * sZ) - (cZ * sY)), 0,
            s.Y * ((cZ * sY * sX) - (cY * sZ)), s.Y * (cX * cZ), s.Y * ((cY * cZ * sX) + (sY * sZ)), 0,
            s.Z * (cX * sY), s.Z * (-sX), s.Z * (cY * cX), 0,
            1, 2, 3, 1);

        var actual = transform.Matrix4X4();

        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                Assert.Equal(expected[row, column], actual[row, column], 4);
    }

    /// <summary>The model matrix places a local point where <see cref="Transform.AddParent"/> does.</summary>
    [Fact]
    public void Matrix4X4AgreesWithAddParent()
    {
        var point = new Transform { Position = new(0.5f, -2, 1) };

        AssertClose(point.AddParent(Child).Position, Vector3.Transform(point.Position, Child.Matrix4X4()));
    }

    /// <summary>Removing a parent undoes adding it, for non-commuting rotations and non-uniform scale.</summary>
    [Fact]
    public void RemoveParentInvertsAddParent()
    {
        var local = Child.AddParent(Parent).RemoveParent(Parent);

        AssertClose(Child.Position, local.Position);
        AssertClose(Child.Orientation, local.Orientation);
        AssertClose(Child.Scale, local.Scale);
    }

    /// <summary>Setting a child's global transform stores the matching local one under its parent.</summary>
    [Fact]
    public void SettingGlobalTransformStoresLocalUnderParent()
    {
        var parent = new Node { Transform = Parent };
        var child = new Node { Parent = parent };
        parent.Children.Add(child);
        var global = Child.AddParent(Parent);

        child.GlobalTransform = global;

        AssertClose(Child.Position, child.Position);
        AssertClose(Child.Orientation, child.Orientation);
        AssertClose(global.Position, child.GlobalTransform.Position);
    }

    /// <summary>Editing an ancestor refreshes a descendant whose global transform was already cached.</summary>
    [Fact]
    public void EditingAncestorRefreshesCachedDescendant()
    {
        var root = new Node();
        var middle = new Node { Parent = root, Position = Vector3.UnitX };
        var leaf = new Node { Parent = middle, Position = Vector3.UnitY };
        root.Children.Add(middle);
        middle.Children.Add(leaf);
        Assert.Equal(new Vector3(1, 1, 0), leaf.GlobalTransform.Position);

        root.Position = new Vector3(0, 0, 5);

        Assert.Equal(new Vector3(1, 1, 5), leaf.GlobalTransform.Position);
    }

    /// <summary>Reparenting a node refreshes its cached global transform.</summary>
    [Fact]
    public void ReparentingRefreshesGlobalTransform()
    {
        var first = new Node { Position = Vector3.UnitX };
        var second = new Node { Position = Vector3.UnitZ };
        var child = new Node { Parent = first };
        Assert.Equal(Vector3.UnitX, child.GlobalTransform.Position);

        child.Parent = second;

        Assert.Equal(Vector3.UnitZ, child.GlobalTransform.Position);
    }

    /// <summary>Formatting applies the format to the vectors and falls back to the default text without one.</summary>
    [Fact]
    public void ToStringFormatsVectors()
    {
        var transform = new Transform { Position = new(1.5f, 0, 0) };
        var invariant = CultureInfo.InvariantCulture;

        Assert.Contains("<1.50, 0.00, 0.00>", transform.ToString("F2", invariant));
        Assert.Equal(transform.ToString(), transform.ToString(null, invariant));
        Assert.Equal(transform.ToString(), transform.ToString("F1", null));
    }

    /// <summary>A node's transform survives a save and load, keeping the identity defaults for missing members.</summary>
    [Fact]
    public void TransformRoundTripsThroughSerializer()
    {
        var node = new Node { Transform = Child };

        var loaded = Serializer.LoadData<Node>(Serializer.Serialize(node))!;
        var partial = Serializer.LoadData<Transform>("""{ "Position": { "X": 1, "Y": 2, "Z": 3 } }""");

        Assert.Equal(Child, loaded.Transform);
        Assert.Equal(new Transform { Position = new(1, 2, 3) }, partial);
    }
}
