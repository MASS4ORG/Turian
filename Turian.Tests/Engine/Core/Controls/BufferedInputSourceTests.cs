using Silk.NET.Input;
using MouseButton = Silk.NET.Input.MouseButton;

namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="BufferedInputSource"/>, which backs both the standalone runtime's Silk.NET
/// input and the Studio's Avalonia-fed play-mode input.
/// </summary>
public class BufferedInputSourceTests
{
    /// <summary>A press edge is reported for exactly one frame; the held state persists.</summary>
    [Fact]
    public void KeyPress_IsReportedForOneFrameOnly()
    {
        var input = new BufferedInputSource();

        input.PushKeyDown(Key.Space);

        Assert.True(input.WasKeyPressed(Key.Space));
        Assert.True(input.IsKeyDown(Key.Space));

        input.NewFrame();

        Assert.False(input.WasKeyPressed(Key.Space));
        Assert.True(input.IsKeyDown(Key.Space));
    }

    /// <summary>A release edge is reported for one frame and clears the held state.</summary>
    [Fact]
    public void KeyRelease_ClearsHeldStateAndIsReportedForOneFrame()
    {
        var input = new BufferedInputSource();

        input.PushKeyDown(Key.A);
        input.NewFrame();
        input.PushKeyUp(Key.A);

        Assert.True(input.WasKeyReleased(Key.A));
        Assert.False(input.IsKeyDown(Key.A));

        input.NewFrame();

        Assert.False(input.WasKeyReleased(Key.A));
    }

    /// <summary>Key auto-repeat must not produce a second press edge.</summary>
    [Fact]
    public void RepeatedKeyDown_DoesNotProduceASecondPressEdge()
    {
        var input = new BufferedInputSource();

        input.PushKeyDown(Key.D);
        input.NewFrame();
        input.PushKeyDown(Key.D); // auto-repeat while still held

        Assert.False(input.WasKeyPressed(Key.D));
        Assert.True(input.IsKeyDown(Key.D));
    }

    /// <summary>Mouse movement accumulates into the frame delta and resets on the next frame.</summary>
    [Fact]
    public void MouseMove_AccumulatesDeltaWithinAFrame()
    {
        var input = new BufferedInputSource();

        input.PushMouseMove(new Vector2(10, 10));
        input.PushMouseMove(new Vector2(15, 20));

        Assert.Equal(new Vector2(15, 20), input.MousePosition);
        Assert.Equal(new Vector2(15, 20), input.MouseDelta);

        input.NewFrame();

        Assert.Equal(new Vector2(15, 20), input.MousePosition);
        Assert.Equal(Vector2.Zero, input.MouseDelta);
    }

    /// <summary>Scroll notches accumulate within a frame and reset afterwards.</summary>
    [Fact]
    public void MouseScroll_AccumulatesWithinAFrame()
    {
        var input = new BufferedInputSource();

        input.PushMouseScroll(1f);
        input.PushMouseScroll(2f);

        Assert.Equal(3f, input.MouseScrollDelta);

        input.NewFrame();

        Assert.Equal(0f, input.MouseScrollDelta);
    }

    /// <summary>Losing focus drops held keys so they do not stay stuck down.</summary>
    [Fact]
    public void Clear_DropsHeldState()
    {
        var input = new BufferedInputSource();

        input.PushKeyDown(Key.W);
        input.PushMouseDown(MouseButton.Left);

        input.Clear();

        Assert.False(input.IsKeyDown(Key.W));
        Assert.False(input.WasKeyPressed(Key.W));
        Assert.False(input.IsMouseButtonDown(MouseButton.Left));
    }
}
