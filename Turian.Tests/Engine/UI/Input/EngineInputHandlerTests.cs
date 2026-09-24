using Silk.NET.Input;
using MouseButton = Silk.NET.Input.MouseButton;

namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="EngineInputHandler"/>, which bridges Guinevere's
/// <c>IInputHandler</c> to the engine's <see cref="Input"/> facade / <see cref="IInputSource"/>.
/// </summary>
public sealed class EngineInputHandlerTests : IDisposable
{
    readonly BufferedInputSource source = new();
    readonly EngineInputHandler handler = new();

    /// <summary>Routes the engine input facade at a controllable buffered source.</summary>
    public EngineInputHandlerTests() => RuntimeServices.Configure(new SingleService(source));

    /// <inheritdoc/>
    public void Dispose() => RuntimeServices.Reset();

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
        source.PushMouseDown(MouseButton.Right);

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
        RuntimeServices.Reset();

        Assert.False(handler.IsKeyDown(GKey.A));
        Assert.True(handler.IsKeyUp(GKey.A));
        Assert.Equal(Vector2.Zero, handler.MousePosition);
        Assert.Equal(0f, handler.MouseWheelDelta);
    }

    sealed class SingleService(object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType.IsInstanceOfType(instance) ? instance : null;
    }
}
