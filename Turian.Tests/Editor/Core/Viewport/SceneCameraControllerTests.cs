namespace Turian.Tests;

/// <summary>
/// Tests for viewport navigation. The camera's yaw convention is left-handed (yaw 0 → Front +Z)
/// while the view matrix is right-handed, so the sign relating mouse movement to yaw is easy to get
/// backwards and impossible to notice in a unit test that only checks the angle changed.
/// </summary>
public class SceneCameraControllerTests
{
    static SceneCameraController FlyController(out EditorCamera camera)
    {
        camera = new EditorCamera { Position = new Vector3(0f, 0f, 0f) };
        var controller = new SceneCameraController(camera) { IsRightButton = true };
        controller.OnMouseDown(100f, 100f);
        return controller;
    }

    /// <summary>Dragging the mouse right must turn the view right, not left.</summary>
    [Fact]
    public void FlyLook_DraggingRight_TurnsTheViewRight()
    {
        var controller = FlyController(out var camera);
        var screenRight = camera.Right;

        controller.OnMouseMove(140f, 100f);

        // The new forward direction must lean toward what was on the right of the screen.
        Assert.True(
            Vector3.Dot(camera.Front, screenRight) > 0f,
            $"Front {camera.Front} did not turn toward screen-right {screenRight}");
    }

    /// <summary>Dragging the mouse left must turn the view left.</summary>
    [Fact]
    public void FlyLook_DraggingLeft_TurnsTheViewLeft()
    {
        var controller = FlyController(out var camera);
        var screenRight = camera.Right;

        controller.OnMouseMove(60f, 100f);

        Assert.True(
            Vector3.Dot(camera.Front, screenRight) < 0f,
            $"Front {camera.Front} did not turn away from screen-right {screenRight}");
    }

    /// <summary>Dragging the mouse up must tilt the view up.</summary>
    [Fact]
    public void FlyLook_DraggingUp_TiltsTheViewUp()
    {
        var controller = FlyController(out var camera);

        controller.OnMouseMove(100f, 60f);

        Assert.True(camera.Pitch > 0f, $"Pitch {camera.Pitch} did not rise");
    }

    /// <summary>
    /// A camera aimed with Euler yaw/pitch has no roll, so the world up axis stays vertical on
    /// screen. This is the framing the screenshot CLI and serialized cameras use.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(90f)]
    [InlineData(210f)]
    public void SetYawPitch_DoesNotRollTheView(float degrees)
    {
        var camera = new EditorCamera();
        camera.SetYawPitch(degrees * MathF.PI / 180f, -20f * MathF.PI / 180f);

        var view = camera.GetViewMatrix() with { M41 = 0f, M42 = 0f, M43 = 0f };
        var upInView = Vector3.Transform(new Vector3(0f, 1f, 0f), view);

        Assert.Equal(0f, upInView.X, 5);
    }

    /// <summary>
    /// A horizontal drag rotates about the camera's own up axis, so the view direction stays in the
    /// plane the screen horizontal spans. Rotating about world up instead sweeps a cone — the arc
    /// that made pitched-down navigation feel wrong.
    /// </summary>
    [Fact]
    public void FlyLook_HorizontalDrag_DoesNotSweepAConeWhenPitched()
    {
        var controller = FlyController(out var camera);
        controller.OnMouseMove(100f, 40f);       // pitch up first
        var localUp = camera.Up;
        var pitchBefore = Vector3.Dot(camera.Front, localUp);

        controller.OnMouseMove(220f, 40f);       // then drag purely horizontally

        // Rotating about the camera's own up cannot change the view's tilt relative to that axis.
        Assert.Equal(pitchBefore, Vector3.Dot(camera.Front, localUp), 4);
    }

    /// <summary>
    /// Dragging up keeps going past vertical instead of stopping at 90°, and the camera ends up
    /// looking behind and below itself rather than jamming.
    /// </summary>
    [Fact]
    public void FlyLook_DraggingFarUp_PassesOverTheTop()
    {
        var controller = FlyController(out var camera);
        var startFront = camera.Front;

        // Sensitivity is 0.005 rad/px, so 40 000 px of travel is a little over one full turn.
        for (var i = 0; i < 200; i++)
        {
            controller.OnMouseMove(100f, 100f - ((i + 1) * 200f));
        }

        Assert.True(camera.Front.Y is > -1.01f and < 1.01f);
        Assert.True(
            Vector3.Dot(camera.Front, startFront) < 0.99f,
            "the camera never left its starting direction, so rotation is still clamped");
    }

    /// <summary>Rotation is free enough to end up upside down, which world-up yaw cannot reach.</summary>
    [Fact]
    public void FlyLook_CanEndUpUpsideDown()
    {
        var controller = FlyController(out var camera);

        // Half a turn of pitch: 180° / 0.005 rad per px = 36 000 px.
        for (var i = 0; i < 180; i++)
        {
            controller.OnMouseMove(100f, 100f - ((i + 1) * 200f));
        }

        Assert.True(camera.Up.Y < 0f, $"camera up {camera.Up} is not inverted after half a turn");
    }

    /// <summary>Q/E move along the camera's own up axis, so they stay useful when it is rolled.</summary>
    [Fact]
    public void ApplyKeyboardMovement_MovesUpAlongTheCameraOwnAxis()
    {
        var camera = new EditorCamera { Position = Vector3.Zero };
        camera.RollLocal(MathF.PI / 2f);
        var controller = new SceneCameraController(camera);
        var localUp = camera.Up;

        controller.ApplyKeyboardMovement(
            [/* Q */ 1], dt: 1f,
            moveForward: 10, moveBackward: 11, moveLeft: 12, moveRight: 13,
            moveUp: 1, moveDown: 2);

        var moved = Vector3.Normalize(camera.Position);
        Assert.Equal(1f, Vector3.Dot(moved, localUp), 3);
    }

    /// <summary>
    /// Orbit sweeps the camera position around the pivot while keeping its distance — that arc is
    /// the mode's whole point, and it must not drift toward or away from the pivot.
    /// </summary>
    [Fact]
    public void OrbitLook_KeepsTheCameraAtItsDistanceFromThePivot()
    {
        var camera = new EditorCamera { Position = new Vector3(0f, 0f, -10f) };
        var controller = new SceneCameraController(camera) { IsLeftButton = true, IsAlt = true };
        controller.OnMouseDown(100f, 100f);
        var pivot = controller.OrbitPivot;
        var distance = (camera.Position - pivot).Length();

        controller.OnMouseMove(160f, 130f);

        Assert.Equal(distance, (camera.Position - pivot).Length(), 3);
    }
}

/// <summary>
/// Tests for choosing which camera the runtime renders through. Every camera in a scene draws to
/// the same target, so picking more than one shows up as flicker.
/// </summary>
public class CameraSelectionTests
{
    static Node CameraNode(string name, int priority, bool active = true)
    {
        var node = new Node { Name = name, IsActive = active };
        node.AddComponent(new CameraComponent { Priority = priority });
        return node;
    }

    /// <summary>The highest priority wins when a scene holds several cameras.</summary>
    [Fact]
    public void FindPrimary_PicksTheHighestPriority()
    {
        var root = new Node { Name = "root" };
        root.Children.Add(CameraNode("low", 0));
        root.Children.Add(CameraNode("main", 100));
        root.Children.Add(CameraNode("mid", 10));

        var primary = CameraComponent.FindPrimary(root);

        Assert.Equal(100, primary!.Priority);
    }

    /// <summary>
    /// Cameras on inactive nodes are ignored, which is how a scene parks fixed benchmark viewpoints
    /// without them fighting the gameplay camera.
    /// </summary>
    [Fact]
    public void FindPrimary_IgnoresCamerasOnInactiveNodes()
    {
        var root = new Node { Name = "root" };
        root.Children.Add(CameraNode("parked", 500, active: false));
        root.Children.Add(CameraNode("main", 1));

        var primary = CameraComponent.FindPrimary(root);

        Assert.Equal(1, primary!.Priority);
    }

    /// <summary>A hierarchy with no camera yields none rather than throwing.</summary>
    [Fact]
    public void FindPrimary_WithNoCamera_ReturnsNull()
    {
        Assert.Null(CameraComponent.FindPrimary(new Node { Name = "root" }));
        Assert.Null(CameraComponent.FindPrimary(null));
    }
}
