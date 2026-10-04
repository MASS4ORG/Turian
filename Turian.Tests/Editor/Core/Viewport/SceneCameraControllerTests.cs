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

    /// <summary>Dragging up turns the view toward world up.</summary>
    [Fact]
    public void FlyLook_DraggingUp_RaisesPitch()
    {
        var controller = FlyController(out var camera);

        controller.OnMouseMove(100f, 60f);

        Assert.True(camera.Front.Y > 0f, $"Front {camera.Front} did not turn upward");
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

    /// <summary>Horizontal look changes world yaw while preserving pitch and a level horizon.</summary>
    [Fact]
    public void FlyLook_HorizontalDrag_PreservesPitchAndLevelHorizon()
    {
        var controller = FlyController(out var camera);
        controller.OnMouseMove(100f, 40f);
        var pitchBefore = camera.Pitch;

        controller.OnMouseMove(220f, 40f);

        Assert.Equal(pitchBefore, camera.Pitch, 4);
        Assert.Equal(0f, camera.Right.Y, 5);
        Assert.True(camera.Up.Y > 0f);
    }

    /// <summary>Large vertical drags stop short of the poles and reverse immediately.</summary>
    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void FlyLook_LargeVerticalDrag_ClampsPitchAndCanReverse(float direction)
    {
        var controller = FlyController(out var camera);
        controller.OnMouseMove(100f, 100f + direction * 40000f);

        Assert.True(MathF.Abs(camera.Pitch) < MathF.PI / 2f);
        Assert.True(camera.Up.Y > 0f);
        Assert.Equal(-direction, MathF.Sign(camera.Pitch));
        var clampedPitch = MathF.Abs(camera.Pitch);

        controller.OnMouseMove(100f, 100f + direction * 39990f);

        Assert.True(MathF.Abs(camera.Pitch) < clampedPitch);
    }

    /// <summary>Repeated mixed drags keep the horizon level and the camera upright.</summary>
    [Fact]
    public void FlyLook_RepeatedMixedDrags_StaysUpright()
    {
        var controller = FlyController(out var camera);

        for (var i = 0; i < 180; i++)
        {
            controller.OnMouseMove(100f + (i + 1) * 50f, 100f - (i + 1) * 200f);
            Assert.True(camera.Up.Y > 0f);
            Assert.Equal(0f, camera.Right.Y, 5);
        }
    }

    /// <summary>The same pointer travel gives the same orientation from any press position.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(480f, 270f)]
    [InlineData(850f, 490f)]
    public void FlyLook_UsesPointerDeltaRegardlessOfPressPosition(float x, float y)
    {
        var controller = FlyController(out var camera);
        camera.SetYawPitch(0.7f, -0.3f);
        var position = camera.Position;
        controller.OnMouseDown(x, y);
        controller.OnMouseMove(x, y);
        Assert.Equal(0.7f, camera.Yaw, 4);
        Assert.Equal(-0.3f, camera.Pitch, 4);

        controller.OnMouseMove(x + 40f, y + 20f);

        Assert.Equal(0.5f, camera.Yaw, 4);
        Assert.Equal(-0.4f, camera.Pitch, 4);
        Assert.Equal(position, camera.Position);
    }

    /// <summary>Starting another drag records its press position without rotating the camera.</summary>
    [Fact]
    public void FlyLook_NewGesture_DoesNotJump()
    {
        var controller = FlyController(out var camera);
        controller.OnMouseMove(150f, 130f);
        controller.SetActiveButton(null);
        controller.OnMouseUp();
        var orientation = camera.Orientation;

        controller.SetActiveButton(ViewportButton.Right);
        controller.OnMouseDown(800f, 500f);
        controller.OnMouseMove(800f, 500f);

        Assert.True(MathF.Abs(Quaternion.Dot(orientation, camera.Orientation)) > 0.99999f);
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
