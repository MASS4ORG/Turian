namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="EngineInputHandler"/>, which bridges Guinevere's
/// <c>IInputHandler</c> to an explicitly supplied <see cref="IInputSource"/>.
/// </summary>
public sealed class EngineInputHandlerTests
{
    readonly BufferedInputSource source = new();
    readonly EngineInputHandler handler;

    /// <summary>Supplies a controllable buffered input source directly.</summary>
    public EngineInputHandlerTests() => handler = new EngineInputHandler(source);

    /// <summary>Guinevere key codes map onto the engine's Silk key codes (shared GLFW values).</summary>
    [Fact]
    public void KeyState_IsForwardedAndMapped()
    {
        source.PushKeyDown(Key.Space);

        Assert.True(handler.IsKeyDown(GKey.Space));
        Assert.True(handler.IsKeyPressed(GKey.Space));
        Assert.False(handler.IsKeyUp(GKey.Space));

        source.NewFrame();

        Assert.True(handler.IsKeyDown(GKey.Space));
        Assert.False(handler.IsKeyPressed(GKey.Space));
    }

    /// <summary>Left/Right/Middle mouse buttons share values between Guinevere and Silk.</summary>
    [Fact]
    public void MouseButtonState_IsForwardedAndMapped()
    {
        source.PushMouseDown(SilkMouseButton.Right);

        Assert.True(handler.IsMouseButtonDown(GMouseButton.Right));
        Assert.True(handler.IsMouseButtonPressed(GMouseButton.Right));
        Assert.True(handler.IsMouseButtonUp(GMouseButton.Left));
    }

    /// <summary>Pointer position, frame delta, previous position and wheel are forwarded.</summary>
    [Fact]
    public void PointerAndWheel_AreForwarded()
    {
        source.PushMouseMove(new Vector2(10, 20));
        source.NewFrame();
        source.PushMouseMove(new Vector2(30, 25));
        source.PushMouseScroll(2f);

        Assert.Equal(new Vector2(30, 25), handler.MousePosition);
        Assert.Equal(new Vector2(20, 5), handler.MouseDelta);
        Assert.Equal(new Vector2(10, 20), handler.PrevMousePosition);
        Assert.Equal(2f, handler.MouseWheelDelta);
    }

    /// <summary>
    /// When the UI is rasterized at a non-1.0 canvas scale, pointer coordinates are converted from
    /// physical viewport pixels to the logical units the layout is hit-tested in.
    /// </summary>
    [Fact]
    public void PointerPosition_IsDividedByCanvasScale()
    {
        handler.CanvasScale = 0.5f;

        source.PushMouseMove(new Vector2(100, 40));
        source.NewFrame();
        source.PushMouseMove(new Vector2(120, 60));

        Assert.Equal(new Vector2(240, 120), handler.MousePosition);   // 120/0.5, 60/0.5
        Assert.Equal(new Vector2(40, 40), handler.MouseDelta);        // (20,20)/0.5
        Assert.Equal(new Vector2(200, 80), handler.PrevMousePosition); // (100,40)/0.5
    }

    /// <summary>Typed characters are reported until the frame ends, then cleared.</summary>
    [Fact]
    public void TypedCharacters_BufferClearsOnEndFrame()
    {
        handler.SetTypedCharacters("hi");
        Assert.Equal("hi", handler.GetTypedCharacters());

        handler.EndFrame();
        Assert.Equal(string.Empty, handler.GetTypedCharacters());
    }

    /// <summary>With no input source configured, every query returns a neutral value.</summary>
    [Fact]
    public void NoSource_YieldsNeutralValues()
    {
        var neutral = new EngineInputHandler();

        Assert.False(neutral.IsKeyDown(GKey.A));
        Assert.True(neutral.IsKeyUp(GKey.A));
        Assert.False(neutral.IsMouseButtonPressed(GMouseButton.Left));
        Assert.True(neutral.IsMouseButtonUp(GMouseButton.Left));
        Assert.Equal(Vector2.Zero, neutral.MousePosition);
        Assert.Equal(0f, neutral.MouseWheelDelta);
    }

    /// <summary>A world panel shares keys and wheel with its source but only clicks when hovered.</summary>
    [Fact]
    public void WorldPanel_ForwardsInputOnlyWhenPointerIsOverPanel()
    {
        var world = new WorldPanelInputHandler(source);
        source.PushKeyDown(Key.Space);
        source.PushMouseDown(SilkMouseButton.Left);
        source.PushMouseScroll(1f);

        Assert.True(world.IsKeyDown(GKey.Space));
        Assert.Equal(1f, world.MouseWheelDelta);
        Assert.False(world.IsMouseButtonDown(GMouseButton.Left));

        world.SetPointer(new Vector2(10, 20));
        Assert.True(world.IsMouseButtonDown(GMouseButton.Left));
        Assert.True(world.IsMouseButtonPressed(GMouseButton.Left));

        world.SetPointer(null);
        Assert.False(world.IsMouseButtonDown(GMouseButton.Left));
    }

    /// <summary>World panels with no source never consult global gameplay input.</summary>
    [Fact]
    public void WorldPanel_WithoutSourceIsNeutral()
    {
        var world = new WorldPanelInputHandler();
        world.SetPointer(new Vector2(10, 20));
        Assert.False(world.IsKeyDown(GKey.Space));
        Assert.True(world.IsKeyUp(GKey.Space));
        Assert.False(world.IsMouseButtonDown(GMouseButton.Left));
        Assert.Equal(0f, world.MouseWheelDelta);
    }
}
