namespace Turian.Tests;

/// <summary>Checks that authored camera transforms immediately control viewing and world-space movement.</summary>
public sealed class CameraTransformTests
{
    /// <summary>Inspector or gizmo rotations update all viewing axes, including roll and inherited rotation.</summary>
    [Theory]
    [InlineData(30, 0, 0, false)]
    [InlineData(0, 90, 0, false)]
    [InlineData(0, 0, 45, false)]
    [InlineData(30, 45, 60, true)]
    public void NodeRotationUpdatesViewingAxes(float x, float y, float z, bool rotateParent)
    {
        var parent = new Node { Position = new Vector3(3, 4, 5) };
        var node = new Node { Parent = parent, Position = new Vector3(0, 0, 2) };
        parent.Children.Add(node);
        var camera = new CameraComponent();
        node.AddComponent(camera);
        var original = camera.GetViewMatrix();
        node.Rotation = new Vector3(x, y, z);
        if (rotateParent) parent.Rotation = new Vector3(15, 30, 20);
        var transform = node.GlobalTransform;
        Assert.True(Vector3.Distance(transform.Forward, camera.Front) < 1e-5f);
        Assert.True(Vector3.Distance(-transform.Right, camera.Right) < 1e-5f);
        Assert.True(Vector3.Distance(transform.Up, camera.Up) < 1e-5f);
        Assert.True(Vector3.Distance(transform.Position, camera.Position) < 1e-5f);
        Assert.NotEqual(original, camera.GetViewMatrix());
        var center = camera.Project(camera.Position + camera.Front * 10);
        Assert.True(center.Length() < 1e-5f);
        camera.Position = new Vector3(5, 6, 7);
        Assert.True(Vector3.Distance(new Vector3(5, 6, 7), node.GlobalTransform.Position) < 1e-5f);
    }

    /// <summary>Positive API pitch tilts upward and attaching a camera preserves its authored transform.</summary>
    [Fact]
    public void AnglePropertiesPreserveUpwardPitchAndAuthoredOrientation()
    {
        var camera = new CameraComponent { Pitch = 0.3f, Yaw = 0.5f };
        Assert.True(camera.Front.Y > 0);
        Assert.True(camera.Front.X > 0);
        var node = new Node { Rotation = new Vector3(20, 30, 40) };
        var authored = node.Orientation;
        node.AddComponent(camera);
        Assert.Equal(authored, node.Orientation);
        camera.Pitch = 0.4f;
        camera.Yaw = 0.6f;
        Assert.Equal(0.4f, camera.Pitch, 5);
        Assert.Equal(0.6f, camera.Yaw, 5);
        Assert.True(camera.Front.Y > 0);
        Assert.True(camera.Front.X > 0);
        var detached = new CameraComponent();
        Assert.Throws<InvalidOperationException>(() => detached.Position);
        Assert.Throws<InvalidOperationException>(() => detached.Position = Vector3.One);
    }
}
